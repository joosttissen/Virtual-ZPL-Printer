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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Labelary.Abstractions;
using Labelary.Service;
using Microsoft.Extensions.Logging;
using Moq;
using UnitsNet.Units;
using VirtualPrinter.FontService;
using Xunit;
using Xunit.Abstractions;

namespace VirtualPrinter.TcpIntegrationTests
{
	/// <summary>
	/// Integration tests that validate multiple quick consecutive ZPL TCP/IP writes
	/// work as expected. Each write contains a single ZPL label sent over a separate
	/// TCP connection, simulating a real-world burst scenario.
	/// </summary>
	public class ConsecutiveTcpWriteTests : IDisposable
	{
		private readonly ITestOutputHelper _output;
		private TcpListener? _listener;
		private int _port;

		public ConsecutiveTcpWriteTests(ITestOutputHelper output)
		{
			_output = output;
		}

		public void Dispose()
		{
			_listener?.Stop();
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Starts a TCP listener on an available port that captures all received data.
		/// Each accepted connection is read fully and the data is added to the provided collection.
		/// </summary>
		private void StartTestListener(ConcurrentBag<string> receivedData, CancellationToken cancellationToken)
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			_port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_output.WriteLine($"Test TCP listener started on port {_port}.");

			_ = Task.Run(async () =>
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					try
					{
						TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
						_ = Task.Run(async () =>
						{
							try
							{
								using (client)
								using (NetworkStream stream = client.GetStream())
								using (MemoryStream ms = new())
								{
									byte[] buffer = new byte[4096];
									int bytesRead;

									//
									// Read until the client closes the connection.
									//
									while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
									{
										ms.Write(buffer, 0, bytesRead);
									}

									string data = Encoding.UTF8.GetString(ms.ToArray());
									if (!string.IsNullOrEmpty(data))
									{
										receivedData.Add(data);
									}
								}
							}
							catch (Exception ex) when (ex is IOException || ex is OperationCanceledException)
							{
								// Expected when connection is closed
							}
						}, cancellationToken);
					}
					catch (Exception) when (cancellationToken.IsCancellationRequested)
					{
						break;
					}
				}
			}, cancellationToken);
		}

		/// <summary>
		/// Sends a single ZPL label over a new TCP connection, mimicking the behavior 
		/// of the ZplClient.SendStringAsync method.
		/// </summary>
		private static async Task SendZplOverTcpAsync(IPAddress ipAddress, int port, string zpl)
		{
			using TcpClient client = new();
			await client.ConnectAsync(ipAddress, port);
			using NetworkStream stream = client.GetStream();
			byte[] buffer = Encoding.UTF8.GetBytes(zpl);
			await stream.WriteAsync(buffer);
			client.Close();
		}

		/// <summary>
		/// Creates a simple single-label ZPL string with a unique identifier.
		/// </summary>
		private static string CreateSingleLabelZpl(int labelNumber)
		{
			return $"^XA^FO50,50^CFA,40^FDLabel {labelNumber}^FS^XZ";
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

		private static LabelService CreateLabelService()
		{
			var loggerMock = new Mock<ILogger<LabelService>>();
			var configMock = new Mock<ILabelServiceConfiguration>();
			var fontServiceMock = new Mock<IFontService>();

			configMock.Setup(c => c.BaseUrl).Returns("http://api.labelary.com/v1/printers");
			configMock.Setup(c => c.Method).Returns("POST");
			configMock.Setup(c => c.Linting).Returns(false);

			fontServiceMock.Setup(f => f.GetReferencedFontsAsync(It.IsAny<string>()))
				.ReturnsAsync(Enumerable.Empty<IPrinterFont>());
			fontServiceMock.Setup(f => f.ApplyReferencedFontsAsync(It.IsAny<IEnumerable<IPrinterFont>>(), It.IsAny<string>()))
				.ReturnsAsync((IEnumerable<IPrinterFont> _, string zpl) => zpl);

			return new LabelService(loggerMock.Object, configMock.Object, fontServiceMock.Object);
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_TenLabels_AllReceivedSuccessfully()
		{
			// Arrange
			const int labelCount = 10;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			string[] sentLabels = new string[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				sentLabels[i] = CreateSingleLabelZpl(i);
			}

			// Act - Send labels consecutively as fast as possible
			Stopwatch stopwatch = Stopwatch.StartNew();

			for (int i = 0; i < labelCount; i++)
			{
				await SendZplOverTcpAsync(IPAddress.Loopback, _port, sentLabels[i]);
			}

			stopwatch.Stop();
			_output.WriteLine($"Sent {labelCount} labels in {stopwatch.ElapsedMilliseconds}ms.");

			// Wait briefly for all data to be received by the listener
			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert - All labels should have been received
			_output.WriteLine($"Received {receivedData.Count} label(s).");
			Assert.Equal(labelCount, receivedData.Count);

			// Verify each sent label was received exactly once
			string[] receivedArray = receivedData.OrderBy(d => d).ToArray();
			string[] sentSorted = sentLabels.OrderBy(d => d).ToArray();

			for (int i = 0; i < labelCount; i++)
			{
				Assert.Equal(sentSorted[i], receivedArray[i]);
			}
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_TenLabels_AllRenderSuccessfully()
		{
			// Arrange
			const int labelCount = 10;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			LabelService labelService = CreateLabelService();
			ILabelConfiguration config = CreateDefaultLabelConfig();

			string[] sentLabels = new string[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				sentLabels[i] = CreateSingleLabelZpl(i);
			}

			// Act - Send labels consecutively as fast as possible
			for (int i = 0; i < labelCount; i++)
			{
				await SendZplOverTcpAsync(IPAddress.Loopback, _port, sentLabels[i]);
			}

			// Wait briefly for all data to be received by the listener
			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert - All labels should render successfully
			Assert.Equal(labelCount, receivedData.Count);

			foreach (string receivedZpl in receivedData)
			{
				IEnumerable<IGetLabelResponse> results = await labelService.GetLabelsAsync(config, receivedZpl);
				IGetLabelResponse[] labelResponses = results.ToArray();

				Assert.Single(labelResponses);
				Assert.True(labelResponses[0].Result, $"Label rendering failed for ZPL: {receivedZpl}. Error: {labelResponses[0].Error}");
				Assert.NotNull(labelResponses[0].Label);
				Assert.True(labelResponses[0].Label.Length > 0);
				Assert.Equal(1, labelResponses[0].LabelCount);

				// Verify PNG magic bytes
				byte[] pngMagic = [0x89, 0x50, 0x4E, 0x47];
				Assert.True(labelResponses[0].Label.Length >= 4);
				Assert.Equal(pngMagic, labelResponses[0].Label.Take(4).ToArray());
			}
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_TenLabels_CompletesWithinOneSecond()
		{
			// Arrange
			const int labelCount = 10;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			string[] sentLabels = new string[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				sentLabels[i] = CreateSingleLabelZpl(i);
			}

			// Act - Time the send of 10 consecutive labels
			Stopwatch stopwatch = Stopwatch.StartNew();

			for (int i = 0; i < labelCount; i++)
			{
				await SendZplOverTcpAsync(IPAddress.Loopback, _port, sentLabels[i]);
			}

			stopwatch.Stop();
			_output.WriteLine($"Sending {labelCount} labels took {stopwatch.ElapsedMilliseconds}ms.");

			// Wait for reception
			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert - Sending 10 labels should be fast (well under 1 second)
			Assert.True(stopwatch.Elapsed.TotalSeconds < 1.0,
				$"Sending {labelCount} labels took {stopwatch.Elapsed.TotalSeconds:F3} seconds, expected less than 1 second.");
			Assert.Equal(labelCount, receivedData.Count);
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_EachLabelContainsSingleZplBlock()
		{
			// Arrange - Validate that each TCP write contains exactly one ^XA...^XZ block
			const int labelCount = 10;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			// Act
			for (int i = 0; i < labelCount; i++)
			{
				string zpl = CreateSingleLabelZpl(i);
				await SendZplOverTcpAsync(IPAddress.Loopback, _port, zpl);
			}

			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert - Each received data should contain exactly one ^XA and one ^XZ
			Assert.Equal(labelCount, receivedData.Count);

			foreach (string received in receivedData)
			{
				int xaCount = CountOccurrences(received.ToUpperInvariant(), "^XA");
				int xzCount = CountOccurrences(received.ToUpperInvariant(), "^XZ");

				Assert.Equal(1, xaCount);
				Assert.Equal(1, xzCount);
			}
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_LargeSegmentedLabel_ReceivedCompletely()
		{
			// Arrange - Send a large ZPL label that requires segmentation
			const int labelCount = 5;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			// Create a large label with many fields
			string[] sentLabels = new string[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				StringBuilder sb = new();
				sb.Append("^XA");
				for (int field = 0; field < 50; field++)
				{
					sb.Append($"^FO50,{50 + field * 20}^CFA,15^FDLine {field} of label {i} - Additional padding text to make it larger^FS");
				}
				sb.Append("^XZ");
				sentLabels[i] = sb.ToString();
			}

			// Act - Send labels rapidly, segmented (1024 byte chunks like ZplClient does)
			for (int i = 0; i < labelCount; i++)
			{
				await SendZplSegmentedAsync(IPAddress.Loopback, _port, sentLabels[i], segmentSize: 1024);
			}

			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert
			Assert.Equal(labelCount, receivedData.Count);

			string[] receivedArray = receivedData.OrderBy(d => d).ToArray();
			string[] sentSorted = sentLabels.OrderBy(d => d).ToArray();

			for (int i = 0; i < labelCount; i++)
			{
				Assert.Equal(sentSorted[i], receivedArray[i]);
			}
		}

		[Fact]
		public async Task ConsecutiveTcpWrites_ParallelSends_AllReceived()
		{
			// Arrange - Send labels in parallel (not just sequentially) to stress-test
			const int labelCount = 10;
			ConcurrentBag<string> receivedData = [];
			using CancellationTokenSource cts = new();

			StartTestListener(receivedData, cts.Token);

			string[] sentLabels = new string[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				sentLabels[i] = CreateSingleLabelZpl(i);
			}

			// Act - Send all labels in parallel
			Stopwatch stopwatch = Stopwatch.StartNew();
			Task[] sendTasks = new Task[labelCount];
			for (int i = 0; i < labelCount; i++)
			{
				int idx = i;
				sendTasks[i] = SendZplOverTcpAsync(IPAddress.Loopback, _port, sentLabels[idx]);
			}
			await Task.WhenAll(sendTasks);
			stopwatch.Stop();

			_output.WriteLine($"Sent {labelCount} labels in parallel in {stopwatch.ElapsedMilliseconds}ms.");

			await Task.Delay(500);
			await cts.CancelAsync();

			// Assert - All labels should be received
			Assert.Equal(labelCount, receivedData.Count);

			string[] receivedArray = receivedData.OrderBy(d => d).ToArray();
			string[] sentSorted = sentLabels.OrderBy(d => d).ToArray();

			for (int i = 0; i < labelCount; i++)
			{
				Assert.Equal(sentSorted[i], receivedArray[i]);
			}
		}

		/// <summary>
		/// Sends ZPL over TCP in segments, mimicking the ZplClient behavior.
		/// </summary>
		private static async Task SendZplSegmentedAsync(IPAddress ipAddress, int port, string zpl, int segmentSize = 1024)
		{
			using TcpClient client = new();
			await client.ConnectAsync(ipAddress, port);
			using NetworkStream stream = client.GetStream();

			IEnumerable<string> segments = zpl
				.Select((c, i) => new { c, i })
				.GroupBy(x => x.i / segmentSize)
				.Select(g => string.Join("", g.Select(y => y.c)));

			foreach (string segment in segments)
			{
				byte[] buffer = Encoding.UTF8.GetBytes(segment);
				await stream.WriteAsync(buffer);
			}

			client.Close();
		}

		private static int CountOccurrences(string text, string pattern)
		{
			int count = 0;
			int index = 0;
			while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
			{
				count++;
				index += pattern.Length;
			}
			return count;
		}
	}
}
