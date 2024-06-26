using Accord.Math;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Avalon {
    public partial class UniversalPolarAlignment : IDisposable {
        private readonly SerialPort port;

        public UniversalPolarAlignment() {
            var comPorts = SerialPort.GetPortNames();
            foreach (var comPort in comPorts) {
                var serialPortToTest = new SerialPort() {
                    PortName = comPort,
                    BaudRate = 115200,
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One
                };

                serialPortToTest.ReadTimeout = 1000;
                serialPortToTest.WriteTimeout = 1000;

                try {
                    serialPortToTest.Open();
                    if (serialPortToTest.IsOpen) {
                        serialPortToTest.WriteLine("?");
                        var status = serialPortToTest.ReadLine();
                        _ = serialPortToTest.ReadLine();
                        var match = StatusRegex().Match(status);
                        if (match.Success) {
                            port = serialPortToTest;
                            break;
                        } else {
                            serialPortToTest.Close();
                            serialPortToTest.Dispose();
                            continue;
                        }
                    }
                } catch { }
            }
            if (port == null) {
                throw new Exception("Unable to find Avalon Polar Alignment System");
            }
            UpdateStatus();
        }

        public bool Connected => port.IsOpen;
        public string Status { get; private set; }

        private float XPosition { get; set; }
        private float YPosition { get; set; }
        private float ZPosition { get; set; }

        public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection ZLastDirection { get; private set; } = LastDirection.Positive;

        public float XPosition1 { get => XPosition / XGearRatio; }
        public float YPosition1 { get => YPosition / YGearRatio; }
        public float ZPosition1 { get => ZPosition / ZGearRatio; }

        public float XGearRatio { get; set; } = 2f;
        public float YGearRatio { get; set; } = 22f;
        public float ZGearRatio { get; set; } = 1;

        private SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        public async Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var target = checkProperty() + position * gearRatio;

                switch(axis) {
                    case Axis.XAxis: XLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }
                
                var command = $"$J=G91G21{axisCommand}{(position * gearRatio).ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                port.WriteLine(command);
                var ok = port.ReadLine();


                while (Math.Abs(checkProperty() - target) > 0.01f) {
                    UpdateStatus();
                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }
        public async Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var target = position * gearRatio;

                switch (axis) {
                    case Axis.XAxis: XLastDirection = position - XPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position - YPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position - ZPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }
                var command = $"$J=G53{axisCommand}{target.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                port.WriteLine(command);
                var ok = port.ReadLine();

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                while (Math.Abs(checkProperty() - target) > 0.01f) {
                    UpdateStatus();
                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }

        private void UpdateStatus() {
            port.WriteLine("?");
            var status = port.ReadLine();
            port.ReadLine();

            var match = StatusRegex().Match(status);
            if (match.Success) {
                Status = match.Groups["status"].Value;
                XPosition = float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
                YPosition = float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
                ZPosition = float.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture);
            } else {
                Logger.Error($"Failed to parse UPA status: {status}");
            }
        }

        [GeneratedRegex(@"<(?<status>\w+)\|MPos:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?),(?<z>[+-]?\d+(\.\d+)?)\|")]
        private static partial Regex StatusRegex();

        public void Dispose() => port?.Dispose();

        public enum Axis {
            XAxis,
            YAxis,
            ZAxis
        }

        public enum LastDirection {
            Negative,
            Positive
        }
    }
}
