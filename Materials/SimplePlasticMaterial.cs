﻿/**
Copyright 2014-2015 Robert McNeel and Associates

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
using System.Drawing;
using System.Runtime.InteropServices;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Render;

namespace RhinoCycles.Materials
{
	[Guid("2D2F02B1-FF94-4434-A0BD-E73B71761BA3")]
	public class SimplePlasticMaterial : RenderMaterial, ICyclesMaterial
	{
		public override string TypeName
		{
			get { return "Cycles Simple Plastic"; }
		}

		public override string TypeDescription
		{
			get { return "Cycles Simple Plastic Material"; }
		}

		public float Gamma { get; set; }

		public CyclesShader.CyclesMaterial MaterialType { get { return CyclesShader.CyclesMaterial.SimplePlastic; } }

		public SimplePlasticMaterial()
		{
			Fields.Add("diffuse", Color4f.White, "Color");
			Fields.Add("polish-amount", 0.0f, "Polish");
			Fields.Add("frost-amount", 0.0f, "Frost");
			Fields.Add("transparency", 0.0f, "Transparency");
			Fields.Add("reflectivity", 0.0f, "Reflectivity");
		}

		public override void SimulateMaterial(ref Material simulatedMaterial, bool forDataOnly)
		{
			base.SimulateMaterial(ref simulatedMaterial, forDataOnly);

			simulatedMaterial.FresnelReflections = true;
			simulatedMaterial.IndexOfRefraction = 1.5;

			float f;

			if (Fields.TryGetValue("polish-amount", out f))
				simulatedMaterial.ReflectionGlossiness = 1.0f - f;

			if (Fields.TryGetValue("frost-amount", out f))
				simulatedMaterial.RefractionGlossiness = f;

			if (Fields.TryGetValue("transparency", out f))
				simulatedMaterial.Transparency = f;

			if (Fields.TryGetValue("reflectivity", out f))
				simulatedMaterial.Reflectivity = f;

			Color4f color;
			if (Fields.TryGetValue("diffuse", out color))
				simulatedMaterial.DiffuseColor = color.AsSystemColor();

			simulatedMaterial.Name = Name;
		}

		public override Material SimulateMaterial(bool isForDataOnly)
		{
			var m = base.SimulateMaterial(isForDataOnly);

			SimulateMaterial(ref m, isForDataOnly);

			return m;
		}

		public string MaterialXml
		{
			get
			{
				Color4f color;
				Fields.TryGetValue("diffuse", out color);
				color = Color4f.ApplyGamma(color, Gamma);

				float reflectivity;
				Fields.TryGetValue("reflectivity", out reflectivity);

				float transparency;
				Fields.TryGetValue("transparency", out transparency);

				float frost_amount;
				Fields.TryGetValue("frost-amount", out frost_amount);

				float polish_amount;
				Fields.TryGetValue("polish-amount", out polish_amount);
				polish_amount = 1.0f - polish_amount;

				return string.Format(
					"<glossy_bsdf name=\"base_glossy\" color=\"{0} {1} {2}\" roughness=\"{4}\" />" +
					"<diffuse_bsdf name=\"base_diffuse\" color=\"{0} {1} {2}\" />" +
					"<glossy_bsdf name=\"coat_glossy\" color=\"1 1 1\" roughness=\"{4}\"/>" +
					"<glass_bsdf name=\"glass\" color=\"{0} {1} {2}\" ior=\"1.09\" roughness=\"{5}\" />" +
					"<transparent_bsdf name=\"transparent\" color=\"1 1 1\" />" +

					"<layer_weight blend=\"0.3\" name=\"base_weight\" />" +
					"<layer_weight blend=\"0.5\" name=\"coat_weight\" />" +
					"<light_path name=\"lp\" />" +

					"<math type=\"Multiply\" use_clamp=\"true\" name=\"base_reflectivity\" value2=\"{3}\" />" +
					"<math type=\"Multiply\" use_clamp=\"true\" name=\"coat_reflectivity\" value2=\"{3}\" />" +
					"<math type=\"Maximum\" name=\"max\" />" +
					"<math type=\"Multiply\" name=\"lp_transparency\" value2=\"{6}\" />" +

					"<mix_closure name=\"base\" />" +
					"<mix_closure name=\"coat\" />" +
					"<mix_closure name=\"transp_factor\" fac=\"{6}\" />" +
					"<mix_closure name=\"lp_factor\" />" +

					// base connections
					"<connect from=\"base_weight fresnel\" to=\"base_reflectivity value1\" />" +
					"<connect from=\"base_reflectivity value\" to=\"base fac\" />" +
					"<connect from=\"base_diffuse bsdf\" to=\"base closure1\" />" +
					"<connect from=\"base_glossy bsdf\" to=\"base closure2\" />" +

					// coat connections
					"<connect from=\"base closure\" to=\"coat closure1\" />" +

					"<connect from=\"coat_weight fresnel\" to=\"coat_reflectivity value1\" />" +
					"<connect from=\"coat_reflectivity value\" to=\"coat fac\" />" +
					"<connect from=\"coat_glossy bsdf\" to=\"coat closure2\" />" +

					// transparency
					"<connect from=\"glass bsdf\" to=\"transp_factor closure2\" />" +
					"<connect from=\"coat closure\" to=\"transp_factor closure1\" />" +

					// light transport
					"<connect from=\"lp isshadowray\" to=\"max value1\" />" +
					"<connect from=\"lp isreflectionray\" to=\"max value2\" />" +

					"<connect from=\"max value\" to=\"lp_transparency value1\" />" +
					"<connect from=\"lp_transparency value\" to=\"lp_factor fac\" />" +

					"<connect from=\"transparent bsdf\" to=\"lp_factor closure2\" />" +
					"<connect from=\"transp_factor closure\" to=\"lp_factor closure1\" />" +

					// connect to output
					"<connect from=\"lp_factor closure\" to=\"output surface\" />" +

					"",
					color.R, color.G, color.B,
					reflectivity,
					polish_amount,
					frost_amount,
					transparency
					);
			}
		}
	}
}
