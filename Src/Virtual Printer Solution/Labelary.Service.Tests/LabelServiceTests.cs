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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Labelary.Abstractions;
using Labelary.Service;
using Microsoft.Extensions.Logging;
using Moq;
using UnitsNet.Units;
using VirtualPrinter.FontService;
using Xunit;

namespace Labelary.Service.Tests
{
	public class LabelServiceTests
	{
		private readonly Mock<ILogger<LabelService>> _loggerMock;
		private readonly Mock<ILabelServiceConfiguration> _configMock;
		private readonly Mock<IFontService> _fontServiceMock;
		private readonly LabelService _labelService;

		public LabelServiceTests()
		{
			_loggerMock = new Mock<ILogger<LabelService>>();
			_configMock = new Mock<ILabelServiceConfiguration>();
			_fontServiceMock = new Mock<IFontService>();

			_configMock.Setup(c => c.BaseUrl).Returns("http://api.labelary.com/v1/printers");
			_configMock.Setup(c => c.Method).Returns("POST");
			_configMock.Setup(c => c.Linting).Returns(false);

			_fontServiceMock.Setup(f => f.GetReferencedFontsAsync(It.IsAny<string>()))
				.ReturnsAsync(Enumerable.Empty<IPrinterFont>());
			_fontServiceMock.Setup(f => f.ApplyReferencedFontsAsync(It.IsAny<IEnumerable<IPrinterFont>>(), It.IsAny<string>()))
				.ReturnsAsync((IEnumerable<IPrinterFont> _, string zpl) => zpl);

			_labelService = new LabelService(_loggerMock.Object, _configMock.Object, _fontServiceMock.Object);
		}

		private static ILabelConfiguration CreateDefaultLabelConfig()
		{
			return new LabelConfiguration
			{
				LabelWidth = 4,
				LabelHeight = 6,
				Unit = LengthUnit.Inch,
				Dpmm = 8,
				LabelRotation = 0,
				LabelFilters = Enumerable.Empty<ILabelFilter>().ToList()
			};
		}

		[Fact]
		public async Task GetLabelsAsync_SimpleLabel_ReturnsOneLabel()
		{
			// Arrange
			string zpl = "^xa^cfa,50^fo100,100^fdHello World^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
			Assert.NotNull(labelResponses[0].Label);
			Assert.True(labelResponses[0].Label.Length > 0);
			Assert.Equal(1, labelResponses[0].LabelCount);
			Assert.Equal(0, labelResponses[0].LabelIndex);
			Assert.Null(labelResponses[0].Error);
		}

		[Fact]
		public async Task GetLabelsAsync_MultipleLabels_ReturnsAllLabels()
		{
			// Arrange - Two labels in one ZPL string
			string zpl = "^xa^cfa,50^fo100,100^fdLabel 1^fs^xz^xa^cfa,50^fo100,100^fdLabel 2^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Equal(2, labelResponses.Length);
			Assert.True(labelResponses[0].Result);
			Assert.True(labelResponses[1].Result);
			Assert.Equal(2, labelResponses[0].LabelCount);
			Assert.Equal(2, labelResponses[1].LabelCount);
			Assert.Equal(0, labelResponses[0].LabelIndex);
			Assert.Equal(1, labelResponses[1].LabelIndex);
		}

		[Fact]
		public async Task GetLabelAsync_SpecificIndex_ReturnsCorrectLabel()
		{
			// Arrange
			string zpl = "^xa^cfa,50^fo100,100^fdLabel 1^fs^xz^xa^cfa,50^fo100,100^fdLabel 2^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IGetLabelResponse result = await _labelService.GetLabelAsync(config, zpl, 1);

			// Assert
			Assert.True(result.Result);
			Assert.NotNull(result.Label);
			Assert.True(result.Label.Length > 0);
			Assert.Equal(1, result.LabelIndex);
		}

		[Fact]
		[Trait("Category", "WindowsOnly")]
		public async Task GetLabelAsync_IndexOutOfRange_ReturnsError()
		{
			// This test exercises ErrorImage.Create which uses System.Drawing.Common (Windows-only).
			if (!OperatingSystem.IsWindows())
			{
				return;
			}

			// Arrange
			string zpl = "^xa^cfa,50^fo100,100^fdHello^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IGetLabelResponse result = await _labelService.GetLabelAsync(config, zpl, 5);

			// Assert
			Assert.False(result.Result);
			Assert.NotNull(result.Error);
			Assert.Contains("out of range", result.Error);
		}

		[Fact]
		public async Task GetLabelsAsync_LabelWithBarcode_RendersPngSuccessfully()
		{
			// Arrange
			string zpl = "^XA^FO50,50^BCN,100,Y,N,N^FD123456789012^FS^XZ";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
			Assert.NotNull(labelResponses[0].Label);

			// Verify it's a valid PNG (PNG magic bytes)
			byte[] pngMagic = [0x89, 0x50, 0x4E, 0x47];
			Assert.True(labelResponses[0].Label.Length >= 4);
			Assert.Equal(pngMagic, labelResponses[0].Label.Take(4).ToArray());
		}

		[Fact]
		public async Task GetLabelsAsync_LabelWithQrCode_RendersPngSuccessfully()
		{
			// Arrange
			string zpl = "^XA^FO50,50^BQN,2,5^FDQA,https://example.com^FS^XZ";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
			Assert.NotNull(labelResponses[0].Label);
			Assert.True(labelResponses[0].Label.Length > 0);
		}

		[Fact]
		public async Task GetLabelsAsync_LabelWithGraphicBox_RendersPngSuccessfully()
		{
			// Arrange
			string zpl = "^XA^FO50,50^GB200,200,3^FS^FO100,100^CFB,30^FDTest^FS^XZ";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
		}

		[Fact]
		[Trait("Category", "WindowsOnly")]
		public async Task GetLabelsAsync_EmptyZpl_ReturnsNoLabelsError()
		{
			// This test exercises ErrorImage.Create which uses System.Drawing.Common (Windows-only).
			if (!OperatingSystem.IsWindows())
			{
				return;
			}

			// Arrange
			string zpl = "";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.False(labelResponses[0].Result);
			Assert.NotNull(labelResponses[0].Error);
		}

		[Fact]
		public async Task GetLabelsAsync_MillimeterUnits_WorksCorrectly()
		{
			// Arrange
			string zpl = "^xa^cfa,50^fo100,100^fdHello^fs^xz";
			var config = new LabelConfiguration
			{
				LabelWidth = 101.6,
				LabelHeight = 152.4,
				Unit = LengthUnit.Millimeter,
				Dpmm = 8,
				LabelRotation = 0,
				LabelFilters = Enumerable.Empty<ILabelFilter>().ToList()
			};

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
		}

		[Fact]
		public async Task GetLabelsAsync_ImageFileName_IsPreserved()
		{
			// Arrange
			string zpl = "^FX ImageFileName:my-custom-name\n^xa^cfa,50^fo100,100^fdHello^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.Equal("my-custom-name", labelResponses[0].ImageFileName);
		}

		[Fact]
		public async Task GetLabelsAsync_BurstOfTenLabels_CompletesQuickly()
		{
			// Arrange - Simulate sending 10 labels quickly (burst of 10 labels/second)
			string zpl = "^xa^cfa,50^fo100,100^fdHello World^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			Stopwatch stopwatch = Stopwatch.StartNew();

			Task<IEnumerable<IGetLabelResponse>>[] tasks = new Task<IEnumerable<IGetLabelResponse>>[10];
			for (int i = 0; i < 10; i++)
			{
				tasks[i] = _labelService.GetLabelsAsync(config, zpl);
			}

			IEnumerable<IGetLabelResponse>[] allResults = await Task.WhenAll(tasks);
			stopwatch.Stop();

			// Assert - All labels should render successfully
			foreach (IEnumerable<IGetLabelResponse> results in allResults)
			{
				IGetLabelResponse[] labelResponses = results.ToArray();
				Assert.Single(labelResponses);
				Assert.True(labelResponses[0].Result, $"Label rendering failed: {labelResponses[0].Error}");
			}

			// The 10 labels should complete well within a reasonable time (10 seconds max)
			Assert.True(stopwatch.Elapsed.TotalSeconds < 10,
				$"Rendering 10 labels took {stopwatch.Elapsed.TotalSeconds:F2} seconds, expected less than 10 seconds.");
		}

		[Fact]
		public async Task GetLabelsAsync_DifferentDpmm_WorksCorrectly()
		{
			// Arrange
			string zpl = "^xa^cfa,50^fo100,100^fdHello^fs^xz";
			var config = new LabelConfiguration
			{
				LabelWidth = 4,
				LabelHeight = 6,
				Unit = LengthUnit.Inch,
				Dpmm = 12,
				LabelRotation = 0,
				LabelFilters = Enumerable.Empty<ILabelFilter>().ToList()
			};

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Single(labelResponses);
			Assert.True(labelResponses[0].Result);
		}

		[Fact]
		public async Task GetLabelsAsync_ThreeLabels_ReturnsAllThree()
		{
			// Arrange
			string zpl = "^xa^fo100,100^fdLabel 1^fs^xz^xa^fo100,100^fdLabel 2^fs^xz^xa^fo100,100^fdLabel 3^fs^xz";
			ILabelConfiguration config = CreateDefaultLabelConfig();

			// Act
			IEnumerable<IGetLabelResponse> results = await _labelService.GetLabelsAsync(config, zpl);
			IGetLabelResponse[] labelResponses = results.ToArray();

			// Assert
			Assert.Equal(3, labelResponses.Length);
			for (int i = 0; i < 3; i++)
			{
				Assert.True(labelResponses[i].Result);
				Assert.Equal(i, labelResponses[i].LabelIndex);
				Assert.Equal(3, labelResponses[i].LabelCount);
			}
		}
	}
}
