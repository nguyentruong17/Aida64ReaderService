using System.IO.MemoryMappedFiles;
using System.Xml;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Polly.Retry;
using Polly;

// SCC-1-[0-9]+
// SCPU[0-9]+UTI
// THDD[1-9]+
/*
 * ["SCPUCK", "SCPUUTI", "SUSEDMEM", "SMEMCLK", "SUSEDVMEM", "SGPU1MEMCLK", "SGPU1UTI"]
 * ["TMOBO", "TCHIP", "TCPUDIO", "TGPU1DIO", "TGPU1HOT"]
 * ["FCPU", "FGPU1", "FGPU1GPU2", "FGPU1GPU3"]
 * ["VCPUVDD", "VGPU1"]
 * ["PCPUVDD", "PGPU1"]
*/

class Sensor
{
    public string Id { get; set; }
    public string AidaId { get; set; }
    public string AidaLabel { get; set; }
    public double Value { get; set; }
}

namespace Aida64ReaderService
{
    public class Worker : BackgroundService
    {
        private static readonly string _sysCpuCoreClockPattern = "SCC-1-[0-9]+";
        private static readonly string _sysCpuThreadUtiPattern = "SCPU[0-9]+UTI";
        private static readonly string _tempHddPattern = "THDD[1-9]+";
        private static readonly string _delimitter = "__";

        private readonly Dictionary<string, string> _dictionaryInUsedKeys = new() {
            { "SCPUCK", $"sys_cpu{_delimitter}clock" },
            { "SCPUUTI", $"sys_cpu{_delimitter}utilization" },
            { "SUSEDMEM", $"sys_mem{_delimitter}usage" },
            { "SMEMCLK", $"sys_mem{_delimitter}clock" },
            { "SGPU1CLK", $"sys_gpu{_delimitter}clock" },
            { "SUSEDVMEM", $"sys_gpu{_delimitter}mem_usage" },
            { "SGPU1MEMCLK", $"sys_gpu{_delimitter}mem_clock" },
            { "SGPU1UTI", $"sys_gpu{_delimitter}utilization" },
            { "TMOBO", $"temp_mobo{_delimitter}measure" },
            { "TCHIP", $"temp_chipset{_delimitter}measure" },
            { "TCPUDIO", $"temp_cpu{_delimitter}measure" },
            { "TGPU1DIO", $"temp_gpu{_delimitter}measure" },
            { "TGPU1HOT", $"temp_gpu{_delimitter}hotspot" },
            { "FCPU", $"fan_cpu{_delimitter}measure" },
            { "FGPU1", $"fan_gpu{_delimitter}fan1" },
            { "FGPU1GPU2", $"fan_gpu{_delimitter}fan2" },
            { "FGPU1GPU3", $"fan_gpu{_delimitter}fan3" },
            { "VCPUVDD", $"voltage_cpu{_delimitter}measure" },
            { "VGPU1", $"voltage_gpu{_delimitter}measure" },
            { "PCPUVDD", $"wattage_cpu{_delimitter}measure" },
            { "PGPU1", $"wattage_gpu{_delimitter}measure" }
        };

        private readonly Dictionary<string, (string, List<Sensor>)> _dictionaryMultiMeasurement = new()
        {
            [_sysCpuCoreClockPattern] = ($"sys_cpu{_delimitter}clock_core_", new List<Sensor>()), // sys_cpu{_delimitter}clock_core_max, sys_cpu{_delimitter}clock_core_min 
            [_sysCpuThreadUtiPattern] = ($"sys_cpu{_delimitter}utilization_thread_", new List<Sensor>()), // sys_cpu{_delimitter}utilization_thread_max, sys_cpu{_delimitter}utilization_thread_min 
        };

        private readonly int _delayedTimeMs = 1_000;

        private readonly MQTTClientService _mqttClientService;
        private readonly ILogger<Worker> _logger;

        private static readonly RetryPolicy delayRetryIfException = Policy
            .Handle<FileNotFoundException>()
            .WaitAndRetry(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(3, retryAttempt))
            );

        public Worker(
            MQTTClientService mqttClientService,
            ILogger<Worker> logger
        ) =>
        (_mqttClientService, _logger) = (mqttClientService, logger);

        private static string ReadSysInfoFromAida64()
        {
            using var mmf = delayRetryIfException.Execute(() =>
                MemoryMappedFile.OpenExisting("AIDA64_SensorValues")
            );
            // using var mmf = MemoryMappedFile.OpenExisting("AIDA64_SensorValues");
            using var accessor = mmf.CreateViewAccessor();
            var bytes = new byte[accessor.Capacity];

            accessor.ReadArray(0, bytes, 0, bytes.Length);

            int i = bytes.Length - 1;
            while (bytes[i] == 0)
                --i;

            byte[] nonEmptyBytes = new byte[i + 1];
            Array.Copy(bytes, nonEmptyBytes, i + 1);

            return "<root>" + Encoding.ASCII.GetString(nonEmptyBytes).Trim() + "</root>";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // calling services
            await _mqttClientService.ExecuteStartAsync();

            bool connected = _mqttClientService.IsConnected;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!connected)
                {
                    connected = _mqttClientService.IsConnected;
                    Console.WriteLine($"connection is ready? {connected}");

                    if (!connected)
                    {
                        await Task.Delay(5000, stoppingToken);
                    }
                    continue;
                }

                var xmlString = ReadSysInfoFromAida64();

                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlString);

                var nodes = xmlDoc.DocumentElement!.ChildNodes
                    .OfType<XmlNode>()
                    .ToList();

                var filteredNodes = new List<XmlNode>();

                // reset each key's array
                foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                {
                    _dictionaryMultiMeasurement[pattern].Item2.Clear();
                }

                var hddTempSensors = new List<Sensor>();

                // filter out the nodes that have not-parsable doubles
                // add all multi-measurement nodes' values to their corresponnding lists
                // filter out the not-needed nodes
                foreach (var node in nodes)
                {
                    string id = node.SelectSingleNode("id")!.InnerText;

                    var success = Double.TryParse(node.SelectSingleNode("value")!.InnerText, out double value);

                    if (!success) continue;

                    foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                    {
                        Regex regex = new(pattern);

                        if (regex.IsMatch(id))
                        {
                            var li = _dictionaryMultiMeasurement[pattern].Item2;
                            li.Add(new Sensor
                            {
                                Id = id,
                                AidaId = id,
                                AidaLabel = node.SelectSingleNode("label")!.InnerText,
                                Value = value
                            });
                        }
                    }

                    if (_dictionaryInUsedKeys.ContainsKey(id))
                    {
                        filteredNodes.Add(node);
                    }

                    Regex tempHddRegex = new(_tempHddPattern);

                    if (tempHddRegex.IsMatch(id))
                    {
                        hddTempSensors.Add(new Sensor
                        {
                            Id = id,
                            AidaId = id,
                            AidaLabel = node.SelectSingleNode("label")!.InnerText,
                            Value = value
                        });
                    }
                }

                var sensors = filteredNodes
                    .Select(n => new Sensor
                    {
                        Id = _dictionaryInUsedKeys[n.SelectSingleNode("id")!.InnerText],
                        AidaId = n.SelectSingleNode("id")!.InnerText,
                        AidaLabel = n.SelectSingleNode("label")!.InnerText,
                        Value = Double.Parse(n.SelectSingleNode("value")!.InnerText),
                    })
                    .ToList();

                foreach (string pattern in _dictionaryMultiMeasurement.Keys)
                {
                    var prefixKey = _dictionaryMultiMeasurement[pattern].Item1;
 
                    var maxSensor = _dictionaryMultiMeasurement[pattern].Item2
                            .Select((sensor) => new { sensor.Value, Each = sensor })
                            .Aggregate((a, b) => (a.Value > b.Value) ? a : b)
                            .Each;

                    var minSensor = _dictionaryMultiMeasurement[pattern].Item2
                            .Select((sensor) => new { sensor.Value, Each = sensor })
                            .Aggregate((a, b) => (a.Value < b.Value) ? a : b)
                            .Each;

                    sensors.Add(new Sensor
                    {
                        Id = prefixKey + "max",
                        AidaId = maxSensor.AidaId,
                        AidaLabel = maxSensor.AidaLabel,
                        Value = maxSensor.Value,
                    });

                    sensors.Add(new Sensor
                    {
                        Id = prefixKey + "min",
                        AidaId = minSensor.AidaId,
                        AidaLabel = minSensor.AidaLabel,
                        Value = minSensor.Value,
                    });
                }

                for (var i = 0; i < hddTempSensors.Count; i++)
                {
                    var eachTempSensor = hddTempSensors[i];
                    sensors.Add(new Sensor
                    {
                        Id = $"temp_hdd{_delimitter}hdd" + (i + 1).ToString(),
                        AidaLabel = eachTempSensor.AidaLabel,
                        AidaId = eachTempSensor.AidaId,
                        Value = eachTempSensor.Value
                    });
                }

                var json = JsonSerializer.Serialize(new
                {
                    sent = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    delimitter = _delimitter,
                    payload = sensors
                });
                // Console.WriteLine(json.ToString());

                await _mqttClientService.ExecutePublishAsync(json);
                await Task.Delay(_delayedTimeMs, stoppingToken);

                connected = _mqttClientService.IsConnected;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Background Service...");
            await _mqttClientService.ExecuteStopAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }
    }
}
