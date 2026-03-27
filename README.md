# TP1 – Serviços de Monitorização Urbana para One Health
**Sistemas Distribuídos 2025/2026 – UTAD**

---

## Estrutura do Projeto

```
TP1/
├── Shared/
│   └── Protocol.cs          ← Constantes e formato do protocolo
├── Sensor/
│   ├── Sensor.cs             ← Cliente com interface de texto
│   └── Sensor.csproj
├── Gateway/
│   ├── Gateway.cs            ← Servidor intermédi (aceita sensores + liga ao servidor)
│   ├── sensores.csv          ← Configuração de sensores registados
│   └── Gateway.csproj
└── Servidor/
    ├── Servidor.cs           ← Servidor final (armazena dados em CSV por tipo)
    └── Servidor.csproj
```

---

## Protocolo de Comunicação

### Formato geral
```
TIPO|campo1|campo2|...
```
Separador: `|`  
Encoding: UTF-8  
Transporte: TCP (linha por mensagem, terminada em `\n`)

---

### Diálogo SENSOR → GATEWAY

| Mensagem   | Formato                                          | Descrição                          |
|------------|--------------------------------------------------|------------------------------------|
| `HELLO`    | `HELLO\|sensor_id\|zona`                         | Apresentação inicial               |
| `REGISTER` | `REGISTER\|sensor_id\|TEMP,HUM,...`              | Declara tipos de dados suportados  |
| `DATA`     | `DATA\|sensor_id\|zona\|tipo\|valor\|timestamp`  | Envia uma medição                  |
| `HEARTBEAT`| `HEARTBEAT\|sensor_id\|timestamp`                | Sinal de vida periódico (10s)      |
| `VIDEO`    | `VIDEO\|sensor_id\|zona\|info`                   | Solicita stream de vídeo           |
| `BYE`      | `BYE\|sensor_id`                                 | Termina ligação                    |

### Respostas GATEWAY → SENSOR

| Mensagem  | Formato                       | Significado          |
|-----------|-------------------------------|----------------------|
| `WELCOME` | `WELCOME\|sensor_id`          | Aceite               |
| `REJECT`  | `REJECT\|sensor_id\|motivo`   | Recusado             |
| `ACK`     | `ACK\|ref_tipo`               | Confirmação OK       |
| `NACK`    | `NACK\|ref_tipo\|motivo`      | Erro / recusa        |

---

### Diálogo GATEWAY → SERVIDOR

| Mensagem    | Formato                                               | Descrição             |
|-------------|-------------------------------------------------------|-----------------------|
| `GW_HELLO`  | `GW_HELLO\|gw_id`                                    | Apresentação inicial  |
| `GW_SENSOR_START` | `GW_SENSOR_START\|gw_id\|sensor_id\|zona\|ts`  | Início de sessão do sensor |
| `GW_SENSOR_REGISTER` | `GW_SENSOR_REGISTER\|gw_id\|sensor_id\|tipos\|ts` | Registo de tipos ativos |
| `GW_DATA`   | `GW_DATA\|gw_id\|sensor_id\|zona\|tipo\|valor\|ts`   | Encaminha medição     |
| `GW_SENSOR_END` | `GW_SENSOR_END\|gw_id\|sensor_id\|ts`            | Fim de sessão do sensor |
| `GW_BYE`    | `GW_BYE\|gw_id`                                      | Termina ligação       |

### Respostas SERVIDOR → GATEWAY

| Mensagem   | Formato              | Significado    |
|------------|----------------------|----------------|
| `SRV_ACK`  | `SRV_ACK`            | Confirmação OK |
| `SRV_NACK` | `SRV_NACK\|motivo`   | Erro           |

---

## Compilar e Executar

### Pré-requisito
- .NET 8 SDK (`dotnet --version`)

### 1. Compilar tudo
```bash
dotnet build Servidor/Servidor.csproj
dotnet build Gateway/Gateway.csproj
dotnet build Sensor/Sensor.csproj
```

### 2. Iniciar o SERVIDOR (terminal 1)
```bash
cd Servidor
dotnet run
# ou com porta personalizada:
dotnet run -- 9100
```

### 3. Iniciar o GATEWAY (terminal 2)
```bash
cd Gateway
dotnet run
# ou com parâmetros: gateway_port servidor_host servidor_port
dotnet run -- 9000 127.0.0.1 9100
```

### 4. Iniciar o SENSOR (terminal 3)
```bash
cd Sensor
dotnet run
# ou com parâmetros: sensor_id zona gateway_host gateway_port
dotnet run -- S101 ZONA_CENTRO 127.0.0.1 9000
```

---

## Dados persistidos

### Gateway (`Gateway/logs_gateway/`)
- Um ficheiro CSV por tipo de dado (`TEMP.csv`, `HUM.csv`, …)
- Ficheiro `sensores.csv` atualizado com `last_sync` e estado

### Servidor (`Servidor/dados_servidor/`)
- Um ficheiro CSV por tipo de dado (`TEMP.csv`, `RUIDO.csv`, …)
- Cabeçalho: `timestamp,gateway_id,sensor_id,zona,tipo,valor`
- Ficheiro `eventos_sensores.csv` com `START`, `REGISTER` e `END` do sensor

---

## Tipos de dados suportados (exemplos)
`TEMP` | `HUM` | `AR` | `RUIDO` | `PM2.5` | `PM10` | `LUZ` | `VIDEO`

---

## Fases implementadas nesta entrega
- ✅ Fase 1 – Protocolo desenhado e documentado
- ✅ Fase 2 – SERVIDOR, GATEWAY e SENSOR simples com interface de texto
