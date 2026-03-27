using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Servidor
{
    class Servidor
    {
        // Pasta onde os dados ficam gravados, organizada por tipo
        private const string DATA_DIR = "dados_servidor";

        // Mutex por tipo de dado para acesso concorrente seguro a ficheiros
        private static readonly Dictionary<string, Mutex> _fileMutexes = new();
        private static readonly object _mutexLock = new();

        static void Main(string[] args)
        {
            int port = Shared.Protocol.SERVIDOR_PORT;
            if (args.Length > 0 && int.TryParse(args[0], out int p))
                port = p;

            Directory.CreateDirectory(DATA_DIR);

            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.WriteLine($"[SERVIDOR] Erro: porta {port} já está em uso. Fecha a instância anterior ou usa outra porta.");
                return;
            }
            Console.WriteLine($"[SERVIDOR] Aguardando ligações na porta {port}...");

            while (true)
            {
                var client = listener.AcceptTcpClient();
                var t = new Thread(() => HandleGateway(client));
                t.IsBackground = true;
                t.Start();
            }
        }

        static void HandleGateway(TcpClient client)
        {
            string gwId = "DESCONHECIDO";
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    Console.WriteLine($"[SERVIDOR] ← [{ep}] {line}");
                    var parts = Shared.Protocol.Parse(line);
                    var tipo = parts[0];

                    switch (tipo)
                    {
                        case Shared.Protocol.GW_HELLO:
                            // GW_HELLO|gw_id
                            if (parts.Length >= 2) gwId = parts[1];
                            Console.WriteLine($"[SERVIDOR] Gateway '{gwId}' conectado.");
                            writer.WriteLine(Shared.Protocol.SRV_ACK);
                            break;

                        case Shared.Protocol.GW_SENSOR_START:
                            // GW_SENSOR_START|gw_id|sensor_id|zona|timestamp
                            if (parts.Length >= 5)
                            {
                                RegistarEventoSensor(parts[1], parts[2], "START", parts[3], parts[4], "");
                                writer.WriteLine(Shared.Protocol.SRV_ACK);
                            }
                            else
                            {
                                writer.WriteLine(Shared.Protocol.Build(
                                    Shared.Protocol.SRV_NACK, "Formato inválido"));
                            }
                            break;

                        case Shared.Protocol.GW_SENSOR_REGISTER:
                            // GW_SENSOR_REGISTER|gw_id|sensor_id|tipos|timestamp
                            if (parts.Length >= 5)
                            {
                                RegistarEventoSensor(parts[1], parts[2], "REGISTER", "", parts[4], parts[3]);
                                writer.WriteLine(Shared.Protocol.SRV_ACK);
                            }
                            else
                            {
                                writer.WriteLine(Shared.Protocol.Build(
                                    Shared.Protocol.SRV_NACK, "Formato inválido"));
                            }
                            break;

                        case Shared.Protocol.GW_DATA:
                            // GW_DATA|gw_id|sensor_id|zona|tipo_dado|valor|timestamp
                            if (parts.Length >= 7)
                            {
                                string gw        = parts[1];
                                string sensorId  = parts[2];
                                string zona      = parts[3];
                                string tipoDado  = parts[4];
                                string valor     = parts[5];
                                string timestamp = parts[6];

                                GravarDado(gw, sensorId, zona, tipoDado, valor, timestamp);
                                writer.WriteLine(Shared.Protocol.SRV_ACK);
                            }
                            else
                            {
                                writer.WriteLine(Shared.Protocol.Build(
                                    Shared.Protocol.SRV_NACK, "Formato inválido"));
                            }
                            break;

                        case Shared.Protocol.GW_SENSOR_END:
                            // GW_SENSOR_END|gw_id|sensor_id|timestamp
                            if (parts.Length >= 4)
                            {
                                RegistarEventoSensor(parts[1], parts[2], "END", "", parts[3], "");
                                writer.WriteLine(Shared.Protocol.SRV_ACK);
                            }
                            else
                            {
                                writer.WriteLine(Shared.Protocol.Build(
                                    Shared.Protocol.SRV_NACK, "Formato inválido"));
                            }
                            break;

                        case Shared.Protocol.GW_BYE:
                            // GW_BYE|gw_id
                            Console.WriteLine($"[SERVIDOR] Gateway '{gwId}' desconectou.");
                            writer.WriteLine(Shared.Protocol.SRV_ACK);
                            return;

                        default:
                            Console.WriteLine($"[SERVIDOR] Mensagem desconhecida: {line}");
                            writer.WriteLine(Shared.Protocol.Build(
                                Shared.Protocol.SRV_NACK, "Mensagem desconhecida"));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERVIDOR] Erro com gateway '{gwId}': {ex.Message}");
            }
            finally
            {
                client.Close();
                Console.WriteLine($"[SERVIDOR] Ligação com '{gwId}' encerrada.");
            }
        }

        static void GravarDado(string gwId, string sensorId, string zona,
                                string tipoDado, string valor, string timestamp)
        {
            // Um ficheiro por tipo de dado: dados_servidor/TEMP.csv
            var filePath = Path.Combine(DATA_DIR, $"{tipoDado}.csv");
            var mutex = GetOrCreateMutex(tipoDado);

            mutex.WaitOne();
            try
            {
                bool exists = File.Exists(filePath);
                using var sw = new StreamWriter(filePath, append: true, Encoding.UTF8);
                if (!exists)
                    sw.WriteLine("timestamp,gateway_id,sensor_id,zona,tipo,valor");

                sw.WriteLine($"{timestamp},{gwId},{sensorId},{zona},{tipoDado},{valor}");
                Console.WriteLine($"[SERVIDOR] ✔ Dados gravados → {filePath}");
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        static Mutex GetOrCreateMutex(string key)
        {
            lock (_mutexLock)
            {
                if (!_fileMutexes.TryGetValue(key, out var m))
                {
                    m = new Mutex();
                    _fileMutexes[key] = m;
                }
                return m;
            }
        }

        static void RegistarEventoSensor(string gwId, string sensorId, string evento, string zona, string timestamp, string detalhes)
        {
            var filePath = Path.Combine(DATA_DIR, "eventos_sensores.csv");
            var mutex = GetOrCreateMutex("eventos_sensores");

            mutex.WaitOne();
            try
            {
                bool exists = File.Exists(filePath);
                using var sw = new StreamWriter(filePath, append: true, Encoding.UTF8);
                if (!exists)
                    sw.WriteLine("timestamp,gateway_id,sensor_id,evento,zona,detalhes");

                sw.WriteLine($"{timestamp},{gwId},{sensorId},{evento},{zona},{detalhes}");
                Console.WriteLine($"[SERVIDOR] ✔ Evento sensor registado → {evento} ({sensorId})");
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
