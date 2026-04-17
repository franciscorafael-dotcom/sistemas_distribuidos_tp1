using System;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using System.Collections.Generic;

class Sensor
{
    static StreamWriter writer;
    static StreamReader reader;
    static TcpClient cliente;
    static volatile bool emExecucao = true;
    static Mutex mutexSocket = new Mutex();

    static void SairComMensagem(string mensagem)
    {
        Console.WriteLine(mensagem);
        try { cliente?.Close(); } catch { }
        Console.WriteLine("[SENSOR] Prima ENTER para fechar...");
        Console.ReadLine();
    }

    // Todos os tipos de dados definidos no protocolo
    static readonly List<string> TODOS_OS_TIPOS = new List<string>
    {
        "TEMP",
        "HUM",
        "RUIDO",
        "AR",
        "PM2.5",
        "PM10",
        "LUMINOSIDADE"
    };

    // ─────────────────────────────────────────────
    //  Menu principal
    // ─────────────────────────────────────────────
    static void MostrarMenu()
    {
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────┐");
        Console.WriteLine("│             SENSOR MENU              │");
        Console.WriteLine("├──────────────────────────────────────┤");
        Console.WriteLine("│   Escolhe o tipo de medição a enviar │");
        Console.WriteLine("├──────────────────────────────────────┤");
        for (int i = 0; i < TODOS_OS_TIPOS.Count; i++)
            Console.WriteLine($"│   {i + 1}. {TODOS_OS_TIPOS[i],-31}│");
        Console.WriteLine("├──────────────────────────────────────┤");
        Console.WriteLine("│   0. Sair                            │");
        Console.WriteLine("└──────────────────────────────────────┘");
        Console.Write(" > Opção: ");
    }

    // ─────────────────────────────────────────────
    //  Enviar medição
    // ─────────────────────────────────────────────
    static void EnviarMedicao(string sensorId, string tipo)
    {
        Console.Write($"  Valor para {tipo}: ");
        string valor = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(valor))
        {
            Console.WriteLine("  [AVISO] Valor não pode ser vazio.");
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        mutexSocket.WaitOne();
        try
        {
            writer.WriteLine($"DATA|{sensorId}|{tipo}|{valor}|{timestamp}");
            string resp = reader.ReadLine();

            if (resp == null)
            {
                Console.WriteLine("  [SENSOR] Gateway fechou a ligação.");
                emExecucao = false;
            }
            else if (resp == "ACK")
            {
                Console.WriteLine($"  [✓] Enviado: {tipo} = {valor}  ({timestamp})");
            }
            else
            {
                Console.WriteLine($"  [✗] Erro. Resposta do Gateway: {resp}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SENSOR] Erro ao enviar DATA: {ex.Message}");
            emExecucao = false;
        }
        finally
        {
            mutexSocket.ReleaseMutex();
        }
    }

    // ─────────────────────────────────────────────
    //  Enviar BYE
    // ─────────────────────────────────────────────
    static void EnviarBye(string sensorId)
    {
        emExecucao = false;
        mutexSocket.WaitOne();
        try
        {
            writer.WriteLine($"BYE|{sensorId}");
            string resp = reader.ReadLine();
            Console.WriteLine($"  [SENSOR] Resposta: {resp}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SENSOR] Erro ao enviar BYE: {ex.Message}");
        }
        finally
        {
            mutexSocket.ReleaseMutex();
        }
    }

    // ─────────────────────────────────────────────
    //  Main
    // ─────────────────────────────────────────────
    static void Main(string[] args)
    {
        string sensorId = args.Length > 0 ? args[0] : "S101";
        string gatewayIP = args.Length > 1 ? args[1] : "127.0.0.1";
        Console.WriteLine("========================================");
        Console.WriteLine("  SENSOR - Monitorizacao Ambiental");
        Console.WriteLine("========================================");

        // ── Ligação ao Gateway ──
        try
        {
            cliente = new TcpClient(gatewayIP, 5000);
        }
        catch (Exception ex)
        {
            SairComMensagem($"[SENSOR] Erro ao ligar ao Gateway em {gatewayIP}:5000 -> {ex.Message}");
            return;
        }

        NetworkStream stream = cliente.GetStream();
        writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        reader = new StreamReader(stream, Encoding.UTF8);
        Console.WriteLine($"[SENSOR] {sensorId} ligado ao Gateway em {gatewayIP}.");

        // ── Zona (única pergunta inicial) ──
        Console.Write("[SENSOR] Zona do sensor: ");
        string zona = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(zona)) zona = "ZONA_DESCONHECIDA";

        // HELLO declara todos os tipos disponíveis
        string tiposDeclared = string.Join(",", TODOS_OS_TIPOS);
        try
        {
            writer.WriteLine($"HELLO|{sensorId}|{zona}|{tiposDeclared}");
        }
        catch (Exception ex)
        {
            SairComMensagem($"[SENSOR] Erro ao enviar HELLO: {ex.Message}");
            return;
        }

        string resposta;
        try
        {
            resposta = reader.ReadLine();
        }
        catch (Exception ex)
        {
            SairComMensagem($"[SENSOR] Erro ao ler resposta ao HELLO: {ex.Message}");
            return;
        }

        Console.WriteLine($"[SENSOR] Resposta ao HELLO: {resposta}");

        if (resposta != "ACK")
        {
            SairComMensagem($"[SENSOR] Não autorizado pelo Gateway (resposta: {resposta}).");
            return;
        }

        // ── Thread de Heartbeat ──
        Thread heartbeatThread = new Thread(() =>
        {
            while (emExecucao)
            {
                Thread.Sleep(30000);
                if (!emExecucao) break;

                mutexSocket.WaitOne();
                try
                {
                    writer.WriteLine($"HEARTBEAT|{sensorId}");
                    Console.WriteLine("\n  [SENSOR] Heartbeat enviado.");
                    string ackHB = reader.ReadLine();
                    if (ackHB == null)
                    {
                        Console.WriteLine("  [SENSOR] Gateway fechou a ligação durante heartbeat.");
                        emExecucao = false;
                    }
                    else
                    {
                        Console.WriteLine($"  [SENSOR] Resposta ao heartbeat: {ackHB}");
                    }
                }
                catch (Exception ex)
                {
                    if (emExecucao)
                        Console.WriteLine($"  [SENSOR] Erro no heartbeat: {ex.Message}");
                    emExecucao = false;
                }
                finally
                {
                    mutexSocket.ReleaseMutex();
                }
            }
        });
        heartbeatThread.IsBackground = true;
        heartbeatThread.Start();

        // ── Loop do menu ──
        while (emExecucao)
        {
            MostrarMenu();
            string input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) continue;

            if (!int.TryParse(input, out int opcao))
            {
                Console.WriteLine("  [AVISO] Insere um número válido.");
                continue;
            }

            if (opcao == 0)
            {
                EnviarBye(sensorId);
                break;
            }

            if (opcao < 1 || opcao > TODOS_OS_TIPOS.Count)
            {
                Console.WriteLine($"  [AVISO] Opção inválida. Escolhe entre 0 e {TODOS_OS_TIPOS.Count}.");
                continue;
            }

            EnviarMedicao(sensorId, TODOS_OS_TIPOS[opcao - 1]);
        }

        cliente.Close();
        Console.WriteLine("[SENSOR] Desligado.");
    }
}