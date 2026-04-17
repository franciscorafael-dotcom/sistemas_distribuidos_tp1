using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Threading;

class Gateway
{
    static StreamWriter writerServidor;
    static StreamReader readerServidor;
    static string ficheiroCSV = "sensores.csv";
    static Mutex mutexCSV = new Mutex();
    static Mutex mutexServidor = new Mutex();
    static int timeoutSegundos = 90;

    class InfoSensor
    {
        public string Estado;
        public string Zona;
        public List<string> Tipos;
        public string LastSync;
    }

    static Dictionary<string, InfoSensor> CarregarCSV()
    {
        var sensores = new Dictionary<string, InfoSensor>();
        if (!File.Exists(ficheiroCSV)) return sensores;

        foreach (string linha in File.ReadAllLines(ficheiroCSV))
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            string[] partes = linha.Split(':', 5);
            if (partes.Length < 5) continue;

            string tiposStr = partes[3].Trim('[', ']');
            List<string> tipos = new List<string>();
            foreach (string t in tiposStr.Split(','))
            {
                string tt = t.Trim();
                if (!string.IsNullOrEmpty(tt)) tipos.Add(tt);
            }

            sensores[partes[0]] = new InfoSensor
            {
                Estado = partes[1],
                Zona = partes[2],
                Tipos = tipos,
                LastSync = partes[4]
            };
        }
        return sensores;
    }

    static void AtualizarLastSync(string sensorId)
    {
        mutexCSV.WaitOne();
        try
        {
            if (!File.Exists(ficheiroCSV)) return;
            string[] linhas = File.ReadAllLines(ficheiroCSV);
            string agora = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            for (int i = 0; i < linhas.Length; i++)
            {
                if (linhas[i].StartsWith(sensorId + ":"))
                {
                    string[] partes = linhas[i].Split(':', 5);
                    if (partes.Length >= 5)
                    {
                        partes[4] = agora;
                        linhas[i] = string.Join(":", partes);
                    }
                }
            }
            File.WriteAllLines(ficheiroCSV, linhas);
        }
        finally
        {
            mutexCSV.ReleaseMutex();
        }
    }

    static void MarcarSensorDesativado(string sensorId)
    {
        mutexCSV.WaitOne();
        try
        {
            if (!File.Exists(ficheiroCSV)) return;
            string[] linhas = File.ReadAllLines(ficheiroCSV);
            for (int i = 0; i < linhas.Length; i++)
            {
                if (linhas[i].StartsWith(sensorId + ":"))
                {
                    string[] partes = linhas[i].Split(':', 5);
                    if (partes.Length >= 5)
                    {
                        partes[1] = "desativado";
                        linhas[i] = string.Join(":", partes);
                    }
                }
            }
            File.WriteAllLines(ficheiroCSV, linhas);
            Console.WriteLine($"[GATEWAY] Sensor {sensorId} marcado como desativado por timeout.");
        }
        finally
        {
            mutexCSV.ReleaseMutex();
        }
    }

    static void MonitorizarHeartbeats()
    {
        while (true)
        {
            Thread.Sleep(30000);
            Dictionary<string, InfoSensor> sensores;
            mutexCSV.WaitOne();
            try { sensores = CarregarCSV(); }
            finally { mutexCSV.ReleaseMutex(); }

            foreach (var par in sensores)
            {
                string id = par.Key;
                InfoSensor info = par.Value;
                if (info.Estado != "ativo") continue;

                if (DateTime.TryParse(info.LastSync, out DateTime lastSync))
                {
                    double segundosPassados = (DateTime.Now - lastSync).TotalSeconds;
                    if (segundosPassados > timeoutSegundos)
                    {
                        Console.WriteLine($"[GATEWAY] Sensor {id} sem heartbeat há {(int)segundosPassados}s. A desativar.");
                        MarcarSensorDesativado(id);
                    }
                }
            }
        }
    }

    static void TratarSensor(object obj)
    {
        TcpClient clienteSensor = (TcpClient)obj;
        NetworkStream streamSensor = clienteSensor.GetStream();
        StreamReader readerSensor = new StreamReader(streamSensor, Encoding.UTF8);
        StreamWriter writerSensor = new StreamWriter(streamSensor, Encoding.UTF8) { AutoFlush = true };

        string sensorId = "";
        string zona = "";

        try
        {
            string linha;
            while ((linha = readerSensor.ReadLine()) != null)
            {
                Console.WriteLine($"[GATEWAY] Recebido: {linha}");
                string[] partes = linha.Split('|');

                if (partes[0] == "HELLO" && partes.Length == 4)
                {
                    sensorId = partes[1];
                    Dictionary<string, InfoSensor> sensores;
                    mutexCSV.WaitOne();
                    try { sensores = CarregarCSV(); }
                    finally { mutexCSV.ReleaseMutex(); }

                    if (!sensores.ContainsKey(sensorId))
                    {
                        Console.WriteLine($"[GATEWAY] Sensor {sensorId} não registado no CSV.");
                        writerSensor.WriteLine("ERROR");
                        break;
                    }

                    if (sensores[sensorId].Estado != "ativo")
                    {
                        Console.WriteLine($"[GATEWAY] Sensor {sensorId} está '{sensores[sensorId].Estado}'.");
                        writerSensor.WriteLine("ERROR");
                        break;
                    }

                    zona = sensores[sensorId].Zona;
                    Console.WriteLine($"[GATEWAY] Sensor {sensorId} validado. Zona: {zona}");
                    AtualizarLastSync(sensorId);
                    writerSensor.WriteLine("ACK");
                }
                else if (partes[0] == "DATA" && partes.Length == 5)
                {
                    if (string.IsNullOrEmpty(sensorId))
                    {
                        writerSensor.WriteLine("ERROR");
                        continue;
                    }

                    string tipoDado = partes[2];
                    string valor = partes[3];
                    string timestamp = partes[4];

                    Dictionary<string, InfoSensor> sensores;
                    mutexCSV.WaitOne();
                    try { sensores = CarregarCSV(); }
                    finally { mutexCSV.ReleaseMutex(); }

                    if (!sensores.ContainsKey(sensorId) || sensores[sensorId].Estado != "ativo")
                    {
                        Console.WriteLine($"[GATEWAY] Sensor {sensorId} já não está ativo.");
                        writerSensor.WriteLine("ERROR");
                        continue;
                    }

                    if (!sensores[sensorId].Tipos.Contains(tipoDado))
                    {
                        Console.WriteLine($"[GATEWAY] Tipo '{tipoDado}' não suportado pelo sensor {sensorId}.");
                        writerSensor.WriteLine("ERROR");
                        continue;
                    }

                    string msgServidor = $"DATA|{sensorId}|{zona}|{tipoDado}|{valor}|{timestamp}";
                    string respostaServidor;
                    mutexServidor.WaitOne();
                    try
                    {
                        writerServidor.WriteLine(msgServidor);
                        respostaServidor = readerServidor.ReadLine();
                        Console.WriteLine($"[GATEWAY] Servidor respondeu: {respostaServidor}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GATEWAY] Erro na comunicação com o servidor: {ex.Message}");
                        respostaServidor = "ERROR";
                    }
                    finally
                    {
                        mutexServidor.ReleaseMutex();
                    }

                    AtualizarLastSync(sensorId);
                    writerSensor.WriteLine(respostaServidor ?? "ERROR");
                }
                else if (partes[0] == "HEARTBEAT" && partes.Length == 2)
                {
                    string hbId = partes[1];
                    Console.WriteLine($"[GATEWAY] Heartbeat de {hbId}.");
                    AtualizarLastSync(hbId);
                    writerSensor.WriteLine("ACK");
                }
                else if (partes[0] == "BYE" && partes.Length == 2)
                {
                    Console.WriteLine($"[GATEWAY] Sensor {partes[1]} desligou-se.");
                    writerSensor.WriteLine("ACK");
                    break;
                }
                else
                {
                    Console.WriteLine($"[GATEWAY] Mensagem inválida: {linha}");
                    writerSensor.WriteLine("ERROR");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GATEWAY] Erro na thread do sensor {sensorId}: {ex.Message}");
        }
        finally
        {
            clienteSensor.Close();
            Console.WriteLine($"[GATEWAY] Thread do sensor {sensorId} encerrada.");
        }
    }

    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  GATEWAY - Agregacao e Encaminhamento");
        Console.WriteLine("========================================");

        if (!File.Exists(ficheiroCSV))
        {
            Console.WriteLine($"[GATEWAY] AVISO: ficheiro '{ficheiroCSV}' não encontrado.");
            Console.WriteLine("[GATEWAY] Cria o ficheiro com o formato:");
            Console.WriteLine("  sensor_id:estado:zona:[TIPO1,TIPO2]:last_sync");
            Console.WriteLine("  Ex: S101:ativo:ZONA_CENTRO:[TEMP,HUM,RUIDO]:2026-01-01T00:00:00");
        }

        string servidorIP = args.Length > 0 ? args[0] : "127.0.0.1";
        try
        {
            TcpClient clienteServidor = new TcpClient(servidorIP, 6000);
            NetworkStream streamServidor = clienteServidor.GetStream();
            writerServidor = new StreamWriter(streamServidor, Encoding.UTF8) { AutoFlush = true };
            readerServidor = new StreamReader(streamServidor, Encoding.UTF8);
            Console.WriteLine($"[GATEWAY] Ligado ao Servidor em {servidorIP}:6000.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GATEWAY] Erro ao ligar ao Servidor em {servidorIP}:6000 -> {ex.Message}");
            return;
        }

        Thread monitorThread = new Thread(MonitorizarHeartbeats);
        monitorThread.IsBackground = true;
        monitorThread.Start();
        Console.WriteLine("[GATEWAY] Monitorização de heartbeats ativa (timeout: 90s).");

        TcpListener listener = new TcpListener(IPAddress.Any, 5000);
        listener.Start();
        Console.WriteLine("[GATEWAY] A escutar sensores na porta 5000...");

        while (true)
        {
            try
            {
                TcpClient clienteSensor = listener.AcceptTcpClient();
                Console.WriteLine("[GATEWAY] Novo sensor ligado. A criar thread...");
                Thread t = new Thread(TratarSensor);
                t.IsBackground = true;
                t.Start(clienteSensor);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] Erro ao aceitar ligação: {ex.Message}");
            }
        }
    }
}