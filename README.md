# transport-seam-repro

Some networks only allow outbound connections through an HTTP proxy, so reaching a broker means
establishing a CONNECT tunnel before AMQP starts. The RabbitMQ AMQP 1.0 .NET client cannot be handed
a socket, a stream or a transport, so the only way through is a local forwarder, and the client then
has to be pointed at `127.0.0.1` instead of at the broker. With `amqps` that makes the TLS handshake
validate the broker certificate against the loopback address: usually a clean
`RemoteCertificateNameMismatch`, but if the certificate happens to list `127.0.0.1` among its
subject alternative names the connection comes up green having verified nothing at all about the
broker's identity.

This repository reproduces all of that in three commands, and then shows the same broker reached
correctly through the same proxy using `ConnectionSettings.TransportFactory`.

- The transport factory: [rabbitmq/rabbitmq-amqp-dotnet-client#180](https://github.com/rabbitmq/rabbitmq-amqp-dotnet-client/pull/180)
- The false green: [rabbitmq/rabbitmq-amqp-dotnet-client#181](https://github.com/rabbitmq/rabbitmq-amqp-dotnet-client/issues/181)

## The lab

```
  host                                  docker network "transport-seam-repro"
  ----                                  -------------------------------------

  src/Problem  --.                      .----------.            .----------.
   127.0.0.1:15671 \  CONNECT           |  squid   |  CONNECT   |  broker  |
    (forwarder)     >---- :3128 ------->|  proxy   |----------->|  broker-a|
  src/Fix       ---'                    '----------'   :5671    |  :5671   |
   (TransportFactory)                                          '----------'
                                        allows CONNECT to
                                        broker-a:5671 only     TLS only, no
                                                               plaintext listener
```

The broker has no plaintext AMQP listener and the proxy will open a tunnel to nothing but
`broker-a:5671`, so there is no way to reach the broker that does not go through a tunnel
established before AMQP starts. `broker-a` resolves only on the lab network, never on the host.

Two broker certificates are generated, both signed by `CN=test-lab-ca`, both with subject
`CN=broker-cert`, differing in exactly one entry:

| `BROKER_CERT_VARIANT` | subject alternative names                              |
| --------------------- | ------------------------------------------------------ |
| `normal` (default)    | `DNS:localhost, DNS:broker-a`                          |
| `san-loopback`        | `DNS:localhost, DNS:broker-a, IP Address:127.0.0.1`    |

That one entry is the difference between demonstration 1 and demonstration 2.

## Prerequisites

- Docker with Compose v2
- .NET SDK 8.0 or later
- `openssl` and `bash`, to generate the lab certificates

## Setup

```bash
./generate-certs.sh
docker compose up -d
```

Demonstrations 1 and 2 need nothing else. Demonstration 3 references the patched client as a project
reference, which needs **one extra step**: a checkout of the client with the pull request #180 branch
on it, next to this repository.

```bash
git clone https://github.com/rabbitmq/rabbitmq-amqp-dotnet-client.git ../rabbitmq-amqp-dotnet-client
git -C ../rabbitmq-amqp-dotnet-client fetch origin pull/180/head:transport-factory
git -C ../rabbitmq-amqp-dotnet-client checkout transport-factory
```

If your checkout lives somewhere else, point at it instead of moving it:

```bash
dotnet run --project src/Fix -p:AmqpClientRepository=/path/to/rabbitmq-amqp-dotnet-client
```

> The captured output below was taken with `PROXY_PORT=13128`, because the machine it ran on already
> had something bound to 3128. With the defaults that line reads `127.0.0.1:3128`. `PROXY_PORT`,
> `BROKER_TLS_PORT` and `MANAGEMENT_PORT` only move the host side of a published port.

## 1. The honest failure: `RemoteCertificateNameMismatch`

The default certificate does not name the loopback address, so the check that is made fails.

```bash
docker compose up -d           # BROKER_CERT_VARIANT defaults to "normal"
dotnet run --project src/Problem
```

```text
transport-seam-repro / Problem
  client          : released RabbitMQ.AMQP.Client, no transport seam
  proxy           : 127.0.0.1:13128 (CONNECT)
  broker          : broker-a:5671
  forwarder       : 127.0.0.1:15671 -> CONNECT broker-a:5671
  client dials    : amqps://127.0.0.1:15671
  TLS target host : 127.0.0.1   <- the address, not the broker's name
  trust anchor    : CN=test-lab-ca
  validation      : strict, no policy error accepted

[forwarder] listening on 127.0.0.1:15671
[proxy] CONNECT broker-a:5671 -> HTTP/1.1 200 Connection established
[tls] certificate subject       : CN=broker-cert
[tls] certificate issuer        : CN=test-lab-ca
[tls] subject alternative names : DNS Name=localhost, DNS Name=broker-a
[tls] SslPolicyErrors           : RemoteCertificateNameMismatch, RemoteCertificateChainErrors
[tls] name checked against      : 127.0.0.1  ->  FAILED
[tls] trust anchor              : the lab authority is not installed in this machine's trust store,
[tls]                             so the chain is resolved against it explicitly instead

RESULT: refused - RemoteCertificateNameMismatch.

  The certificate is well formed and was issued by the lab authority, but it
  does not name 127.0.0.1, and that address is what the
  handshake checked against, because it is the only address the client could
  be given. The broker's real name, broker-a, never took part in the
  check at all.

  This is the honest outcome: inconvenient, and at least not misleading. Run
  the lab again with BROKER_CERT_VARIANT=san-loopback for the outcome that is.
```

## 2. The false green: the same check, passing, for nothing

Change the broker certificate. Change nothing else: same code, same strict validation, same
forwarder.

```bash
BROKER_CERT_VARIANT=san-loopback docker compose up -d
dotnet run --project src/Problem
```

```text
transport-seam-repro / Problem
  client          : released RabbitMQ.AMQP.Client, no transport seam
  proxy           : 127.0.0.1:13128 (CONNECT)
  broker          : broker-a:5671
  forwarder       : 127.0.0.1:15671 -> CONNECT broker-a:5671
  client dials    : amqps://127.0.0.1:15671
  TLS target host : 127.0.0.1   <- the address, not the broker's name
  trust anchor    : CN=test-lab-ca
  validation      : strict, no policy error accepted

[forwarder] listening on 127.0.0.1:15671
[proxy] CONNECT broker-a:5671 -> HTTP/1.1 200 Connection established
[tls] certificate subject       : CN=broker-cert
[tls] certificate issuer        : CN=test-lab-ca
[tls] subject alternative names : DNS Name=localhost, DNS Name=broker-a, IP Address=127.0.0.1
[tls] SslPolicyErrors           : RemoteCertificateChainErrors
[tls] name checked against      : 127.0.0.1  ->  passed
[tls] trust anchor              : the lab authority is not installed in this machine's trust store,
[tls]                             so the chain is resolved against it explicitly instead
[amqp] connection state: Open

RESULT: connected, and the identity check established nothing.

  The hostname check passed, with strict validation configured. No callback
  was relaxed: a name mismatch would have been refused outright, and the only
  error the platform reported is that a throw-away lab authority is not in
  this machine's trust store. Install that authority, as a real deployment
  would have done, and the reported value is SslPolicyErrors.None.

  And yet: the name checked was 127.0.0.1, the address of a
  forwarder running inside this very process. The broker's own name,
  broker-a, took no part in the check. The certificate happens to list
  that loopback address, so the check that exists to detect an impostor could
  not have detected one: any process able to hold
  127.0.0.1:15671 would have satisfied exactly the same check.

  This is the dangerous outcome. Every indicator agrees that TLS is working,
  and the weakest link appears in no log and on no dashboard.
```

This is issue #181. The connection is `Open`, the hostname check passed, and nothing in that
exchange established that the peer was the broker.

## 3. The fix: the tunnel inside the client, the name checked against the broker

Same proxy, same broker, same strict validation, and the default certificate that does *not* name
the loopback address. The application supplies the connected stream; the library keeps TLS, SASL and
the AMQP open, and authenticates against `ConnectionSettings.Host`.

```bash
docker compose up -d           # back to BROKER_CERT_VARIANT=normal
dotnet run --project src/Fix
```

```text
transport-seam-repro / Fix
  client          : RabbitMQ.AMQP.Client with ConnectionSettings.TransportFactory
  proxy           : 127.0.0.1:13128 (CONNECT)
  broker          : broker-a:5671
  client dials    : amqps://broker-a:5671 (through the transport factory)
  TLS target host : broker-a   <- the broker's own name
  trust anchor    : CN=test-lab-ca
  validation      : strict, no policy error accepted

[factory] asked for a transport to broker-a:5671
[proxy] CONNECT broker-a:5671 -> HTTP/1.1 200 Connection established
[tls] certificate subject       : CN=broker-cert
[tls] certificate issuer        : CN=test-lab-ca
[tls] subject alternative names : DNS Name=localhost, DNS Name=broker-a
[tls] SslPolicyErrors           : RemoteCertificateChainErrors
[tls] name checked against      : broker-a  ->  passed
[tls] trust anchor              : the lab authority is not installed in this machine's trust store,
[tls]                             so the chain is resolved against it explicitly instead
[amqp] connection state: Open
[amqp] queue declared: transport-seam-repro-f339d24dd4914b2484c8b8914a45663e
[amqp] published: round-trip-78dd1da6df424b9c948bac23378c2a41 (Accepted)
[amqp] consumed : round-trip-78dd1da6df424b9c948bac23378c2a41

RESULT: round trip OK, through the proxy, against the broker's own name.

  The certificate was checked against broker-a, the name the
  connection was configured for, and no loopback address took part at any
  point. A certificate listing 127.0.0.1 would have bought nothing here,
  because nothing asked about 127.0.0.1 - and the certificate in use for
  this run does not list it.

  The transport factory was invoked 1 time(s):
  one transport for one connection attempt. TLS, SASL and the AMQP open all
  stayed inside the library; the application only produced the byte stream.
```

Note the two lines that carry the whole point: the name checked is `broker-a`, and the certificate
that satisfied it does not list `127.0.0.1`. There is no address in the picture for a loopback SAN
to satisfy.

## Layout

```
docker-compose.yml          the broker and the proxy
generate-certs.sh           the lab CA and the two broker certificate variants
rabbitmq/rabbitmq.conf      TLS only, no plaintext listener
squid/squid.conf            CONNECT to broker-a:5671, nothing else
src/Problem/Program.cs      the loopback forwarder and the released client
src/Fix/Program.cs          the transport factory and the patched client
src/Shared/TlsObserver.cs   the validation callback, and why it is not a relaxed one
src/Shared/ProxyTunnel.cs   the HTTP CONNECT tunnel both apps use
```

## On the validation callback

Both apps use a custom `RemoteCertificateValidationCallback`, which deserves a word, because "the
demonstration relaxed validation" would explain the false green away.

It does not relax validation. It replaces one thing, the trust anchor, so that a throw-away lab
authority does not have to be installed in the machine's trust store; it resolves the chain against
that authority explicitly. Every other check is left exactly as the platform made it, and
`RemoteCertificateNameMismatch` is refused outright and unconditionally. `AcceptablePolicyErrors`
stays at `SslPolicyErrors.None` throughout. That is why demonstration 1 fails: the callback is given
a name mismatch, and refuses. Demonstration 2 passes because the platform found no name mismatch to
report.

See `src/Shared/TlsObserver.cs`.

## Teardown

```bash
docker compose down -v
```
