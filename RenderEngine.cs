﻿/**
Copyright 2014-2017 Robert McNeel and Associates

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
**/

using System;
using System.Drawing;
using System.Threading;
using ccl;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Render;
using RhinoCyclesCore.Core;
using RhinoCyclesCore.Database;
using RhinoCyclesCore.ExtensionMethods;
using sdd = System.Diagnostics.Debug;

namespace RhinoCyclesCore
{

	public enum State
	{
		Waiting,
		Uploading,
		Rendering,
		Stopped
	}

	/// <summary>
	/// The actual render engine, ready for asynchronous work in Rhino.
	/// </summary>
	public partial class RenderEngine : AsyncRenderContext
	{
		protected CreatePreviewEventArgs PreviewEventArgs { get; set; }

		/// <summary>
		/// Reference to the client representation of this render engine instance.
		/// </summary>
		public Client Client { get; set; }

		/// <summary>
		/// True when State.Rendering
		/// </summary>
		public bool IsRendering => State == State.Rendering;

		/// <summary>
		/// True when State.Uploading
		/// </summary>
		public bool IsUploading => State == State.Uploading;

		/// <summary>
		/// True when State.Waiting
		/// </summary>
		public bool IsWaiting => State == State.Waiting;

		/// <summary>
		/// True when State.IsStopped
		/// </summary>
		public bool IsStopped => State == State.Stopped;

		/// <summary>
		/// Current render engine state.
		/// </summary>
		public State State { get; set; }

		/// <summary>
		/// Reference to the session of this render engine instance.
		/// </summary>
		public Session Session = null;

		/// <summary>
		/// Reference to the bitmap we're rendering into.
		/// 
		/// This is used when rendering material previews.
		/// </summary>
		public Bitmap RenderBitmap { get; set; }

		/// <summary>
		/// Set to true when the render session should be cancelled - used for preview job cancellation
		/// </summary>
		public bool CancelRender { get; set; }

		public bool SupportClippingPlanes { get; set; }

		public int RenderedSamples;

		public string TimeString;

		protected CSycles.UpdateCallback m_update_callback;
		protected CSycles.RenderTileCallback m_update_render_tile_callback;
		protected CSycles.RenderTileCallback m_write_render_tile_callback;
		protected CSycles.TestCancelCallback m_test_cancel_callback;
		protected CSycles.DisplayUpdateCallback m_display_update_callback;
		protected CSycles.LoggerCallback m_logger_callback;

		public event EventHandler ChangesReady;
		public void TriggerChangesReady()
		{
			Rhino.RhinoApp.OutputDebugString("Triggering changes, changequeue applied everything\n");
			ChangesReady?.Invoke(this, EventArgs.Empty);
		}

		protected bool m_flush;
		/// <summary>
		/// Flag set to true when a flush on the changequeue is needed.
		///
		/// Setting of Flush is protected with a lock. Getting is not.
		/// </summary>
		public bool Flush
		{
			get
			{
				bool flush;
				flush = m_flush;
				return flush;
			}
			set
			{
				m_flush = value;
			}
		}

		/// <summary>
		/// Our instance of the change queue. This is our access point for all
		/// data. The ChangeQueue mechanism will push data to it, record it
		/// with all necessary book keeping to track the data relations between
		/// Rhino and Cycles.
		/// </summary>
		public ChangeDatabase Database { get; set; }

		/// <summary>
		/// Return true if any change has been received through the changequeue
		/// </summary>
		/// <returns>true if any changes have been received.</returns>
		private bool HasSceneChanges()
		{
			return Database.HasChanges();
		}

		/// <summary>
		/// Check if we should change render engine status. If the changequeue
		/// has notified us of any changes Flush will be true. If we're rendering
		/// then move to State.Halted and cancel our current render progress.
		/// </summary>
		public void CheckFlushQueue()
		{
			if (State == State.Waiting) Continue();
			// not rendering, nor flush needed, bail
			if (State != State.Rendering || Database == null || !Flush) return;

			// We've been told we need to flush, so cancel current render
			//State = State.Halted;
			// flush the queue
			Database.Flush();

			// if we've got actually changes we care about
			// change state to signal need for uploading
			if (HasSceneChanges())
			{
				Session?.Cancel("Scene changes detected.\n");
				//if (!m_interactive) Session?.Cancel("Scene changes detected.\n");
				//else {
				//	// TODO: ensure ViewportRenderEngine doesn't set pause ever. Session?.SetPause(true);
				//}
				State = State.Uploading;
			}

			// reset flush flag directly, since we already have lock.
			m_flush = false;
		}

		protected readonly uint m_doc_serialnumber;
		private readonly bool m_interactive;

		public RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(m_doc_serialnumber);

		/// <summary>
		/// Render engine implementations that need to keep track of views
		/// for instance to signal when a frame is ready for that particular
		/// view.
		/// 
		/// Generally such engines want to register an event handler to
		/// Database.ViewChanged to record the new ViewInfo here.
		/// </summary>
		public ViewInfo View { get; set; }

		public bool ViewSet => View != null;

#region CONSTRUCTORS

		private void RegisterEventHandler()
		{
			Database.MaterialShaderChanged += Database_MaterialShaderChanged;
			Database.LightShaderChanged += Database_LightShaderChanged;
			Database.FilmUpdateTagged += Database_FilmUpdateTagged;
		}

		private void SetKernelFlags()
		{
			CSycles.debug_set_opencl_kernel(RcCore.It.EngineSettings.OpenClKernelType);
			CSycles.debug_set_opencl_single_program(RcCore.It.EngineSettings.OpenClSingleProgram);
			CSycles.debug_set_cpu_kernel(RcCore.It.EngineSettings.CPUSplitKernel);
		}

		public RenderEngine(Guid pluginId, uint docRuntimeSerialNumber, bool interactive)
			: this (pluginId, docRuntimeSerialNumber, new ViewInfo(docRuntimeSerialNumber), null, interactive)
		{
			SetKernelFlags();
		}

		public RenderEngine(Guid pluginId, uint docRuntimeSerialnumber, ViewInfo view, ViewportInfo vp, bool interactive)
		{
			SetKernelFlags();
			SupportClippingPlanes = RcCore.It.EngineSettings.RaytracedClippingPlanes;
			m_doc_serialnumber = docRuntimeSerialnumber;
			View = view;
			m_interactive = interactive;
			Database = new ChangeDatabase(pluginId, this, m_doc_serialnumber, View, !m_interactive)
			{
				SupportClippingPlanes = SupportClippingPlanes
			};
			RegisterEventHandler();
		}

		public RenderEngine(Guid pluginId, CreatePreviewEventArgs previewEventArgs, bool interactive)
		{
			SetKernelFlags();
			SupportClippingPlanes = RcCore.It.EngineSettings.RaytracedClippingPlanes;
			PreviewEventArgs = previewEventArgs;
			Database = new ChangeDatabase(pluginId, this, PreviewEventArgs)
			{
				SupportClippingPlanes = SupportClippingPlanes
			};
			RegisterEventHandler();
		}

#endregion

		/// <summary>
		/// Tell our changequeue instance to initialise world.
		/// </summary>
		public void CreateWorld()
		{
			Database.CreateWorld(RcCore.It.EngineSettings.FlushAtEndOfCreateWorld);
		}

		/// <summary>
		/// True if rendering for preview
		/// </summary>
		/// <returns></returns>
		public bool IsPreview()
		{
			return Database.IsPreview;
		}

		/// <summary>
		/// Flush
		/// </summary>
		public void FlushIt()
		{
			Database.Flush();
		}

		public void TestCancel(uint sid)
		{
			if (IsStopped) return;

			if (PreviewEventArgs != null)
			{
				if (PreviewEventArgs.Cancel)
				{
					CancelRender = true;
					Session.Cancel("Preview Cancelled");
				}
			}
		}

		public class StatusTextEventArgs
		{
			public StatusTextEventArgs(string s, float progress, int samples)
			{
				StatusText = s;
				Progress = progress;
				Samples = samples;
			}

			public string StatusText { get; private set; }
			public float Progress { get; private set; }
			public int Samples { get; private set; }
		}

		public event EventHandler<StatusTextEventArgs> StatusTextUpdated;

		/// <summary>
		/// Tell engine to fire StatusTextEvent with given arguments
		/// </summary>
		/// <param name="e"></param>
		public void TriggerStatusTextUpdated(StatusTextEventArgs e)
		{
			StatusTextUpdated?.Invoke(this, e);
		}

		/// <summary>
		/// Handle status updates
		/// </summary>
		/// <param name="sid"></param>
		public void UpdateCallback(uint sid)
		{
			if (IsStopped) return;

			var status = CSycles.progress_get_status(Client.Id, sid);
			var substatus = CSycles.progress_get_substatus(Client.Id, sid);
			RenderedSamples = CSycles.progress_get_sample(Client.Id, sid);
			float progress;
			double total_time, sample_time;
			CSycles.progress_get_time(Client.Id, sid, out total_time, out sample_time);
			CSycles.progress_get_progress(Client.Id, sid, out progress);
			int hr = ((int)total_time) / (60 * 60);
			int min = (((int)total_time) / 60) % 60;
			int sec = ((int)total_time) % 60;
			int hun = ((int)(total_time * 100.0)) % 100;

			if (!substatus.Equals(string.Empty)) status = status + ": " + substatus;

			TimeString = $"{hr}h {min}m {sec}.{hun}s";

			status = $"{status} {TimeString}";

			// don't set full 100% progress here yet, because that signals the renderwindow the end of async render
			if (progress >= 0.9999f) progress = 1.0f;
			if (RcCore.It.EngineSettings.Samples == ushort.MaxValue) progress = -1.0f;
			RenderWindow?.SetProgress(status, progress);

			TriggerStatusTextUpdated(new StatusTextEventArgs(status, progress, RenderedSamples>0 ? (RenderedSamples+1) : RenderedSamples));

			if(!m_interactive) CheckFlushQueue();
		}

		/// <summary>
		///  Clamp color so we get valid values for system bitmap
		/// </summary>
		/// <param name="ch"></param>
		/// <returns></returns>
		public static int ColorClamp(int ch)
		{
			if (ch < 0) return 0;
			return ch > 255 ? 255 : ch;
		}

		/// <summary>
		/// Update the RenderWindow or RenderBitmap with the updated tile from
		/// Cycles render progress.
		/// </summary>
		/// <param name="sessionId"></param>
		/// <param name="tx"></param>
		/// <param name="ty"></param>
		/// <param name="tw"></param>
		/// <param name="th"></param>
		public void DisplayBuffer(uint sessionId, uint tx, uint ty, uint tw, uint th, PassType passtype, ref float[] pixels, int pixlen, int stride)
		{
			if (IsStopped) return;
			//(var width, var height) =  RenderDimension;
			var width = RenderDimension.Width;
			var height = RenderDimension.Height;
			if (RenderWindow != null)
			{
				using (RenderWindow.Channel channel = RenderWindow.OpenChannel(RenderWindow.StandardChannels.RGBA),
					nx = RenderWindow.OpenChannel(RenderWindow.StandardChannels.NormalX),
					ny = RenderWindow.OpenChannel(RenderWindow.StandardChannels.NormalY),
					nz = RenderWindow.OpenChannel(RenderWindow.StandardChannels.NormalZ),
					depth = RenderWindow.OpenChannel(RenderWindow.StandardChannels.DistanceFromCamera)
					)
				{
					if (channel != null)
					{
						var rect = new Rectangle((int)tx, (int)ty, (int)tw, (int)th);
						for (var x = 0; x < (int)tw; x++)
						{
							for (var y = 0; y < (int)th; y++)
							{
								var i = y * tw * stride + x * stride;
								var r = pixels[i];
								var g = stride > 1 ? pixels[i + 1] : 1.0f;
								var b = stride > 2 ? pixels[i + 2] : 1.0f;
								var a = stride > 3 ? pixels[i + 3] : 1.0f;
								if (stride == 1)
								{
									g = b = r;
								}
								if (passtype != PassType.Combined) a = 1.0f;
								a = 1.0f;

								var c4_f = new Color4f(r, g, b, a);

								var cox = (int)tx + x;
								var coy = height - ((int)ty + y + 1);
								if (channel != null && passtype == PassType.Combined)
								{
									channel.SetValue(cox, coy, c4_f);
								}
								else if (nx!=null && ny!=null && nz!=null && passtype == PassType.Normal) {
									nx.SetValue(cox, coy, r);
									ny.SetValue(cox, coy, g);
									nz.SetValue(cox, coy, b);
								} else if(depth!=null && passtype == PassType.Depth)
								{
									depth.SetValue(cox, coy, r);
								}
							}
						}
						RenderWindow.InvalidateArea(rect);
					}
				}
			}
		}

		/// <summary>
		/// Callback for debug logging facility. Will be called only for Debug builds of ccycles.dll
		/// </summary>
		/// <param name="msg"></param>
		public static void LoggerCallback(string msg)
		{
			sdd.WriteLine($"DBG: {msg}");
		}

		/// <summary>
		/// Handle write render tile callback
		/// </summary>
		/// <param name="sessionId"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <param name="w"></param>
		/// <param name="h"></param>
		/// <param name="depth"></param>
		public void WriteRenderTileCallback(uint sessionId, uint x, uint y, uint w, uint h, uint sample, uint depth, PassType passtype, float[] pixels, int pixlen)
		{
			if (IsStopped) return;
			DisplayBuffer(sessionId, x, y, w, h, passtype, ref pixels, pixlen, (int)depth);
		}

		/// <summary>
		/// Handle update render tile callback
		/// </summary>
		/// <param name="sessionId"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <param name="w"></param>
		/// <param name="h"></param>
		/// <param name="depth"></param>
		public void UpdateRenderTileCallback(uint sessionId, uint x, uint y, uint w, uint h, uint sample, uint depth, PassType passtype, float[] pixels, int pixlen)
		{
#if UGH
			if (IsStopped) return;
			DisplayBuffer(sessionId, x, y, w, h, passtype, ref pixels, pixlen, (int)depth);
#endif
		}

		/// <summary>
		/// Called when user presses the stop render button.
		/// </summary>
		override public void StopRendering()
		{
			base.StopRendering();
			if (RenderThread == null) return;

			System.Diagnostics.Debug.Assert(Session != null);

			StopTheRenderer();
		}

		public void Pause()
		{
			State = State.Waiting;
		}

		public void Continue()
		{
			State = State.Rendering;
		}

		private void StopTheRenderer()
		{
			// signal that we should stop rendering.
			CancelRender = true;

			// set state to stopped
			while (State == State.Uploading)
			{
				Thread.Sleep(10);
			}
			State = State.Stopped;

			// signal our cycles session to stop rendering.
			Session?.Cancel("Render stop called.\n");

			// get rid of our change queue
			Database?.Dispose();
			Database = null;

			JoinRenderThread();
		}

		/// <summary>
		/// Set progress to RenderWindow, if it is not null.
		/// </summary>
		/// <param name="rw"></param>
		/// <param name="msg"></param>
		/// <param name="progress"></param>
		public void SetProgress(RenderWindow rw, string msg, float progress)
		{
			rw?.SetProgress(msg, progress);
		}

		/// <summary>
		/// Register the callbacks to the render engine session
		/// </summary>
		protected void SetCallbacks()
		{
#region register callbacks with Cycles session

			Session.UpdateCallback = m_update_callback;
			Session.UpdateTileCallback = m_update_render_tile_callback;
			Session.WriteTileCallback = m_write_render_tile_callback;
			Session.TestCancelCallback = m_test_cancel_callback;
			Session.DisplayUpdateCallback = m_display_update_callback;

#endregion
		}

		// handle material shader updates
		protected void Database_MaterialShaderChanged(object sender, MaterialShaderUpdatedEventArgs e)
		{
			Converters.BitmapConverter.ReloadTextures(e.RcShader);
			RecreateMaterialShader(e.RcShader, e.CclShader);
			e.CclShader.Tag();
		}

		// handle light shader updates
		protected void Database_LightShaderChanged(object sender, LightShaderUpdatedEventArgs e)
		{
			ReCreateSimpleEmissionShader(e.RcLightShader, e.CclShader);
			e.CclShader.Tag();
		}

		protected void Database_FilmUpdateTagged(object sender, EventArgs e)
		{
			Session.Scene.Film.Update();
		}

		protected void Database_LinearWorkflowChanged(object sender, LinearWorkflowChangedEventArgs e)
		{
		}
	}

}
