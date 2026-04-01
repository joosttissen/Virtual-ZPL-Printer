/*
 *  This file is part of Virtual ZPL Printer.
 *  
 *  Virtual ZPL Printer is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  Virtual ZPL Printer is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with Virtual ZPL Printer.  If not, see <https://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinaryKits.Zpl.Label.Elements;
using BinaryKits.Zpl.Viewer;
using BinaryKits.Zpl.Viewer.ElementDrawers;
using BinaryKits.Zpl.Viewer.Models;
using Labelary.Abstractions;
using Microsoft.Extensions.Logging;
using UnitsNet;
using VirtualPrinter.FontService;

namespace Labelary.Service
{
	public class LabelService(ILogger<LabelService> logger, ILabelServiceConfiguration labelServiceConfiguration, IFontService fontService) : ILabelService
	{
		protected ILogger<LabelService> Logger { get; set; } = logger;
		public ILabelServiceConfiguration LabelServiceConfiguration { get; set; } = labelServiceConfiguration;
		protected IFontService FontService { get; set; } = fontService;

		private readonly IPrinterStorage _printerStorage = new PrinterStorage();

		public async Task<IGetLabelResponse> GetLabelAsync(ILabelConfiguration labelConfiguration, string zpl, int labelIndex = 0)
		{
			this.Logger.LogInformation("Rendering label index {index} using local renderer.", labelIndex);

			IEnumerable<IGetLabelResponse> allLabels = await this.RenderLabelsLocallyAsync(labelConfiguration, zpl);
			IGetLabelResponse[] labelArray = allLabels as IGetLabelResponse[] ?? allLabels.ToArray();

			if (labelIndex < labelArray.Length)
			{
				return labelArray[labelIndex];
			}

			//
			// The requested label index is out of range.
			//
			this.Logger.LogWarning("Label index {index} is out of range. Total labels: {count}.", labelIndex, labelArray.Length);

			GetLabelResponse errorResponse = new()
			{
				LabelIndex = labelIndex,
				LabelCount = labelArray.Length,
				ImageFileName = zpl.GetParameterValue("ImageFileName", "zpl-label-image"),
				Zpl = zpl,
				Result = false,
				Error = $"Label index {labelIndex} is out of range. Total labels: {labelArray.Length}."
			};

			ErrorImage errorImage = ErrorImage.Create(labelConfiguration, "Invalid Index", errorResponse.Error);
			errorResponse.Label = errorImage.ImageData;

			return errorResponse;
		}

		public async Task<IEnumerable<IGetLabelResponse>> GetLabelsAsync(ILabelConfiguration labelConfiguration, string zpl)
		{
			this.Logger.LogDebug("Rendering all labels using local renderer.");
			return await this.RenderLabelsLocallyAsync(labelConfiguration, zpl);
		}

		protected async Task<IEnumerable<IGetLabelResponse>> RenderLabelsLocallyAsync(ILabelConfiguration labelConfiguration, string zpl)
		{
			IList<IGetLabelResponse> returnValue = [];
			string imageFileName = zpl.GetParameterValue("ImageFileName", "zpl-label-image");

			try
			{
				//
				// Apply the filters to the ZPL.
				//
				string filteredZpl = zpl.Filter(labelConfiguration.LabelFilters);
				this.Logger.LogDebug("Filtered ZPL: '{zpl}'.", filteredZpl.Replace("\"", ""));

				//
				// Load fonts that are referenced in the ZPL.
				//
				IEnumerable<IPrinterFont> fonts = await this.FontService.GetReferencedFontsAsync(zpl);
				filteredZpl = await this.FontService.ApplyReferencedFontsAsync(fonts, filteredZpl);

				//
				// Convert label dimensions to millimeters for BinaryKits.Zpl.Viewer.
				//
				double labelWidthMm = new Length(labelConfiguration.LabelWidth, labelConfiguration.Unit).ToUnit(UnitsNet.Units.LengthUnit.Millimeter).Value;
				double labelHeightMm = new Length(labelConfiguration.LabelHeight, labelConfiguration.Unit).ToUnit(UnitsNet.Units.LengthUnit.Millimeter).Value;
				int dpmm = labelConfiguration.Dpmm;

				this.Logger.LogDebug("Label dimensions: {width}mm x {height}mm at {dpmm} dpmm.", labelWidthMm, labelHeightMm, dpmm);

				//
				// Analyze the ZPL to extract label elements.
				//
				ZplAnalyzer analyzer = new(this._printerStorage);
				AnalyzeInfo analyzeInfo = analyzer.Analyze(filteredZpl);
				LabelInfo[] labelInfos = this.FilterSetupOnlyLabels(analyzeInfo.LabelInfos);

				this.Logger.LogDebug("ZPL analysis found {count} label(s).", analyzeInfo.LabelInfos.Length);

				if (analyzeInfo.Errors != null && analyzeInfo.Errors.Length > 0)
				{
					foreach (string error in analyzeInfo.Errors)
					{
						this.Logger.LogWarning("ZPL analysis error: {error}.", error);
					}
				}

				if (analyzeInfo.UnknownCommands != null && analyzeInfo.UnknownCommands.Length > 0)
				{
					this.Logger.LogDebug("ZPL analysis found {count} unknown command(s).", analyzeInfo.UnknownCommands.Length);
				}

				//
				// Render each label.
				//
				DrawerOptions drawerOptions = new()
				{
					OpaqueBackground = true
				};

				ZplElementDrawer drawer = new(this._printerStorage, drawerOptions);
				int totalLabels = labelInfos.Length;

				for (int i = 0; i < totalLabels; i++)
				{
					try
					{
						byte[] imageData = drawer.Draw(
							labelInfos[i].ZplElements,
							labelWidthMm,
							labelHeightMm,
							dpmm);

						this.Logger.LogDebug("Successfully rendered label {index} of {total}.", i, totalLabels);

						GetLabelResponse response = new()
						{
							LabelIndex = i,
							LabelCount = totalLabels,
							Result = true,
							Label = imageData,
							Error = null,
							ImageFileName = imageFileName,
							Zpl = filteredZpl
						};

						returnValue.Add(response);
					}
					catch (Exception ex)
					{
						this.Logger.LogError(ex, "Exception rendering label {index}.", i);

						ErrorImage errorImage = ErrorImage.Create(labelConfiguration, "Render Error", ex.Message);

						GetLabelResponse response = new()
						{
							LabelIndex = i,
							LabelCount = totalLabels,
							Result = false,
							Label = errorImage.ImageData,
							Error = ex.Message,
							ImageFileName = imageFileName,
							Zpl = filteredZpl
						};

						returnValue.Add(response);
					}
				}

				//
				// If no labels were found, create a single empty result.
				//
				if (totalLabels == 0)
				{
					this.Logger.LogWarning("No labels found in the ZPL data.");

					ErrorImage errorImage = ErrorImage.Create(labelConfiguration, "No Labels", "No labels were found in the ZPL data.");

					GetLabelResponse response = new()
					{
						LabelIndex = 0,
						LabelCount = 1,
						Result = false,
						Label = errorImage.ImageData,
						Error = "No labels were found in the ZPL data.",
						ImageFileName = imageFileName,
						Zpl = filteredZpl
					};

					returnValue.Add(response);
				}
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, "Exception during local ZPL rendering.");

				ErrorImage errorImage = ErrorImage.Create(labelConfiguration, "Exception", ex.Message);

				GetLabelResponse response = new()
				{
					LabelIndex = 0,
					LabelCount = 1,
					Result = false,
					Label = errorImage.ImageData,
					Error = ex.Message,
					ImageFileName = imageFileName,
					Zpl = zpl
				};

				returnValue.Add(response);
			}

			return returnValue;
		}

		private LabelInfo[] FilterSetupOnlyLabels(LabelInfo[] labelInfos)
		{
			if (labelInfos.Length < 2)
			{
				return labelInfos;
			}

			LabelInfo[] printableLabels = labelInfos
				.Where(static labelInfo => labelInfo.ZplElements.Any(static element => element is ZplPositionedElementBase || element is ZplReferenceGrid))
				.ToArray();

			if (printableLabels.Length == 0 || printableLabels.Length == labelInfos.Length)
			{
				return labelInfos;
			}

			this.Logger.LogInformation(
				"Skipping {count} setup-only label(s) from a multi-label ZPL payload.",
				labelInfos.Length - printableLabels.Length);

			return printableLabels;
		}
	}
}
