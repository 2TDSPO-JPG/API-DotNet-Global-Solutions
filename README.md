# Saneia — API .NET de monitoramento

A operadora usa esta API para consultar o mapa, a ficha e o histórico dos reservatórios do Saneia. O Java emite o JWT e mantém contas, catálogo e relatos. O .NET consulta o catálogo Java e guarda leituras e eventos no seu próprio esquema de telemetria.

- [Saneia Mobile](https://github.com/2TDSPO-JPG/SaneiaMobile)
- [Contrato HTTP](https://github.com/2TDSPO-JPG/SaneiaMobile/blob/main/docs/api.md)
- [Backend Java](https://github.com/2TDSPO-JPG/API-Java-Global-Solutions)

## Executar no Rider

1. Abra `Saneia.sln` com o SDK .NET 8 instalado.
2. Inicie o Java com a base `http://localhost:8080/api`. Os dois serviços precisam usar a mesma configuração de verificação de token. Na configuração de execução `Saneia.Api` do Rider, defina `Jwt__SharedSecret` com o valor de `JWT_SECRET` usado pelo Java. Para uma execução local do Java sem essa variável, use o segredo de desenvolvimento configurado no perfil local do Java.
3. Execute `Saneia.Api`. O perfil Development usa SQLite e cria suas tabelas automaticamente. A base HTTP é `http://localhost:5085/api`, e a interface interativa fica em [Swagger](http://localhost:5085/swagger).
4. Faça login como operadora em `POST /api/auth/login` no Java. Copie o campo `token` para **Authorize** no Swagger .NET. O arquivo `requests.http` também permite testar pelo cliente HTTP do Rider.
5. Para usar o arquivo HTTP, crie **http-client.private.env.json** ao lado dele no formato mostrado abaixo e selecione o ambiente **local** no Rider.
6. Use **Run All Tests** no projeto `Saneia.Tests`, ou rode `dotnet test` na raiz da solução.

No arquivo privado do cliente HTTP, preencha o valor com a senha de desenvolvimento da operadora.

    { "local": { "operatorPassword": "<senha de desenvolvimento>" } }

Para executar por terminal na raiz, configure o segredo na sessão e rode:

```powershell
$env:Jwt__SharedSecret = Read-Host 'JWT_SECRET do Java' -MaskInput
dotnet run --project src/Saneia.Api
```

O Java atual emite JWT HS256 com emissor `saneia-api`, claim `role` e sem audiência. O .NET verifica assinatura, emissor, expiração, identificador e papel. Se o Java configurar audiência, defina `Jwt__Audience` também no .NET. O compartilhamento do segredo HS256 é a opção de compatibilidade para integração local. A alternativa por validação interna descrita pelo contrato depende de um endpoint que o Java atual não fornece. Para assinatura assimétrica, a API aceita `Jwt__Algorithm=RS256`, `Jwt__PublicKeyPath` (PEM público) e `Jwt__Audience`. Essa alternativa requer que o Java emita tokens RS256 compatíveis.

## Oracle

Use um usuário/esquema de telemetria com as tabelas criadas pelo arquivo `database/oracle-schema.sql`. Execute o script uma vez como esse usuário, pelo SQL Developer, antes de iniciar a API. O script é gerado pelo modelo EF Core. O esquema Java continua responsável pelo cadastro e pelos relatos.

Defina `Database__Provider=Oracle` e `ConnectionStrings__Oracle` na configuração local da execução ou no ambiente da hospedagem. Exemplo de formato da conexão:

```text
User Id=SANEIA_NET;Password=<senha>;Data Source=<host>:1521/<serviço>;
```

As credenciais ficam nas variáveis da execução. O aplicativo não precisa da conexão Oracle.

## Rotas

| Rota | Uso |
| --- | --- |
| `GET /api/sensors?page=0&size=200` | Mapa com catálogo ativo, estado e último volume. Retorna array e cabeçalho `X-Total-Pages`. |
| `GET /api/sensors/{id}` | Ficha, últimas 500 medições e últimos 100 eventos de alarme. |
| `POST /api/sensors/{id}/readings` | Grava uma leitura de teste com o JWT da operadora. `deviceId` deve coincidir com o prefixo do equipamento no Java. |
| `GET /health/live` | Indica se a API está ativa. |
| `GET /health/ready` | Consulta as tabelas de telemetria e o health do Java. Retorna 503 se uma dependência falhar. |

A requisição de gravação recebe `deviceId`, `sampleId`, `at` (ISO 8601), `volume` (litros) e, opcionalmente, `levelCm` e `note`. O banco tem uma restrição única por dispositivo e amostra. A primeira gravação retorna `201`. A repetição exata retorna `200` com o mesmo conteúdo. Reutilizar a chave com dados diferentes, ou enviar uma nova medição fora de ordem, retorna `409`.

Sem medições, o sensor fica `offline` com `volume` e `lastReadingAt` nulos. Se parar de enviar, o estado fica `offline` e a última medição permanece visível. O estado de aviso começa em 80% do limite. Ao atingir o limite, o sistema abre um alarme. Uma sequência de medições críticas mantém o mesmo evento, que é encerrado após duas leituras abaixo do limite. A configuração padrão aceita 120 chamadas de monitoramento por minuto por usuário. Ajuste com `RateLimit__PermitsPerMinute`.

## Conectar o Mobile

No cliente HTTP do Expo, proponha `EXPO_PUBLIC_MONITORING_API_URL=http://localhost:5085/api` para direcionar somente `GET /sensors` e `GET /sensors/{id}` ao .NET. Envie o mesmo Bearer do login. Mantenha `EXPO_PUBLIC_API_URL` para o Java, inclusive para `POST /sensors/{id}/insights`. Preserve a paginação e a leitura do cabeçalho `X-Total-Pages`.

Para Expo Web, configure a origem exata do Expo em `Cors__Origins__0` (e outros índices se necessário). Em um aparelho na mesma rede, use o IP do computador onde roda a API e configure o .NET para ouvir no endereço de rede. Entre redes, use uma URL HTTPS acessível ao aparelho. O repositório do Mobile precisa receber a configuração da segunda URL.

O teste local usa gravações de exemplo pelo endpoint HTTP. A integração do Wokwi/HiveMQ e a leitura de telemetria pelo Java para os insights fazem parte da etapa posterior descrita no contrato.

## Verificação

Os testes xUnit usam SQLite temporário e simulam o catálogo Java para cobrir regras de estado, expiração/assinatura do JWT, papéis, paginação, idempotência concorrente, validação, rate limiting, Health Checks e OpenAPI. O teste manual local usa o Java real e seu JWT no Swagger ou no arquivo de requisições do Rider.
