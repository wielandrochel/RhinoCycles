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

using RhinoCyclesCore.Core;
using Rhino;
using Rhino.Commands;

namespace RhinoCycles.Commands
{
	[System.Runtime.InteropServices.Guid("ff25c46b-45e6-4bc1-8f74-489a248241d9")]
	[CommandStyle(Style.Hidden)]
	public class TestShowCyclesViewportSettingsPanelToggle : Command
	{
		static TestShowCyclesViewportSettingsPanelToggle _instance;
		public TestShowCyclesViewportSettingsPanelToggle()
		{
			if(_instance==null) _instance = this;
		}

		///<summary>The only instance of the ShowCyclesViewportSettingsPanelToggle command.</summary>
		public static TestShowCyclesViewportSettingsPanelToggle Instance => _instance;

		public override string EnglishName => "TestShowCyclesViewportSettingsPanelToggle";

		protected override Result RunCommand(RhinoDoc doc, RunMode mode)
		{
			RcCore.It.EngineSettings.ShowViewportPropertiesPanel = !RcCore.It.EngineSettings.ShowViewportPropertiesPanel;
			var showing = RcCore.It.EngineSettings.ShowViewportPropertiesPanel ? "Showing" : "Not showing";
			RhinoApp.WriteLine($"{showing} custom viewport settings");
			return Result.Success;
		}
	}
}
