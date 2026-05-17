# Manual SMB · 01 — Instalación de Docker + firewall + DNS

> **Audiencia:** operador instalando Verbara Platform desde cero en un servidor Linux fresco.
> **Tiempo:** 30-45 minutos (depende de cuán nuevo es el server).
> **Pre-requisitos:**
> - Acceso root o sudo en un servidor Linux (ver tabla de OS soportados en [00](00-vision-general.md)).
> - Conectividad de salida a internet (para `apt`/`dnf` y para `docker pull`).
> - Saber tu IP pública (`curl https://api.ipify.org` desde el server).

Este manual cubre **3 cosas** que **deben** estar listas antes del arranque del stack en [02](02-arranque-stack.md):

1. **Docker + Docker Compose** instalados y corriendo, usuario operador en el grupo `docker`.
2. **Firewall del host** abriendo los puertos SIP/RTP/Web.
3. **NAT / port-forwarding** del router (si es on-prem detrás de NAT) o **Security Group** del cloud (si es VM).

Opcionalmente: **DNS records** + **certificado TLS** Let's Encrypt para acceso HTTPS al Web UI.

## 0. Verificar tu escenario de red ANTES de empezar

Corré desde el server estos 2 comandos:

```bash
$ ip -4 route get 8.8.8.8 | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") print $(i+1)}'
192.168.40.100         # ← tu IP LAN

$ curl -sS https://api.ipify.org
200.118.42.61          # ← tu IP pública
```

Comparalos:

| LAN vs Pública | Tu escenario | EXTERNAL_IP a setear más tarde |
|---|---|---|
| IGUALES (`200.118.42.61 == 200.118.42.61`) | **A** — IP pública directa en NIC (cloud VM o bare-metal expuesto) | NO setear (vacío) |
| DISTINTAS, LAN privada (`10.x` / `172.16-31.x` / `192.168.x`) | **B** (cloud VM con NIC privada + LB cloud) o **C** (on-prem detrás de router) | `EXTERNAL_IP={tu-IP-pública}` |
| IP pública en rango `100.64.0.0/10` | **D** — CGNAT del ISP | ⚠️ SIP inbound **no funcionará** desde internet — leer abajo §CGNAT |

> 💡 El script `scripts/quickstart-smb.sh` (manual 02) hace esta detección automáticamente, pero saberlo desde ahora te ayuda a configurar el firewall/router correctamente.

## 1. Instalar Docker + Docker Compose

### 1.1 Debian 12 (bookworm) — recomendado

```bash
# Eliminar versiones antiguas si las hay
$ sudo apt remove docker docker-engine docker.io containerd runc 2>/dev/null

# Pre-requisitos
$ sudo apt update
$ sudo apt install -y ca-certificates curl gnupg lsb-release

# Repositorio oficial Docker
$ sudo install -m 0755 -d /etc/apt/keyrings
$ curl -fsSL https://download.docker.com/linux/debian/gpg | \
    sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
$ sudo chmod a+r /etc/apt/keyrings/docker.gpg
$ echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
    https://download.docker.com/linux/debian $(lsb_release -cs) stable" | \
    sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
$ sudo apt update

# Docker Engine + Compose plugin
$ sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Habilitar al arranque y arrancar
$ sudo systemctl enable --now docker

# Agregar tu usuario al grupo docker (sin sudo para `docker` después de re-loguear)
$ sudo usermod -aG docker $USER
$ newgrp docker
```

**Validar:**
```bash
$ docker --version           # → Docker version 27.x.x
$ docker compose version     # → Docker Compose version v2.30.x
$ docker run --rm hello-world
```

### 1.2 Ubuntu 22.04 / 24.04 LTS

Idéntico a Debian 12 pero cambiando el repositorio:
```bash
$ curl -fsSL https://download.docker.com/linux/ubuntu/gpg | \
    sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
$ echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
    https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | \
    sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
```
Resto igual (`apt install docker-ce…`, `systemctl enable docker`, `usermod -aG`).

### 1.3 Rocky Linux 9 / AlmaLinux 9

```bash
$ sudo dnf -y install dnf-plugins-core
$ sudo dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo
$ sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
$ sudo systemctl enable --now docker
$ sudo usermod -aG docker $USER
$ newgrp docker
```

### 1.4 Amazon Linux 2023

```bash
$ sudo dnf -y install docker
$ sudo systemctl enable --now docker
$ sudo usermod -aG docker $USER
$ newgrp docker

# Compose plugin viene aparte — descargar manualmente:
$ DOCKER_CONFIG=${DOCKER_CONFIG:-$HOME/.docker}
$ mkdir -p $DOCKER_CONFIG/cli-plugins
$ curl -SL "https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$(uname -m)" \
    -o $DOCKER_CONFIG/cli-plugins/docker-compose
$ chmod +x $DOCKER_CONFIG/cli-plugins/docker-compose
$ docker compose version
```

## 2. Firewall del host

> ⚠️ **No saltarse este paso.** Aunque uses cloud Security Groups, el firewall del host (UFW/firewalld/nftables) tiene que dejar pasar el tráfico a Docker. De lo contrario el cliente SIP externo conectará al puerto público pero los paquetes nunca llegarán a Asterisk.

### Puertos a abrir (resumen)

| Puerto | Protocolo | Servicio | Obligatorio | Quien lo usa |
|---|---|---|---|---|
| 5060 | UDP | SIP signalling | ✅ | Trunks SIP, softphones |
| 5060 | TCP | SIP signalling | ✅ | Trunks SIP que usan TCP (mejor para NAT) |
| 5061 | TCP | SIP TLS | opt | Trunks que requieren cifrado de signalling |
| 8088 | TCP | Asterisk ARI HTTP | ✅ | Platform.Api → Asterisk |
| 8089 | TCP | Asterisk WSS (WebRTC) | ✅ | Browser de agente → Asterisk |
| 5038 | TCP | Asterisk AMI | opt | Platform.Api → Asterisk (eventos) |
| 80 | TCP | Web UI HTTP | ✅ | Acceso usuarios al Web |
| 443 | TCP | Web UI HTTPS | opt | TLS terminado en Web nginx |
| 20000-20200 | UDP | RTP (audio) | ✅ | 200 puertos = 100 calls; ajustar al tier |
| 4569 | UDP | IAX2 | opt | Solo legacy trunks IAX |

> ⚠️ **El range RTP debe estar 100% abierto.** No abras solo "20000" — son 200 puertos consecutivos, todos UDP. Cada llamada usa 2 (RTP + RTCP).

### 2.1 UFW (Ubuntu default)

```bash
$ sudo ufw allow 5060/udp  comment 'Verbara SIP UDP'
$ sudo ufw allow 5060/tcp  comment 'Verbara SIP TCP'
$ sudo ufw allow 5061/tcp  comment 'Verbara SIP TLS'
$ sudo ufw allow 8088/tcp  comment 'Verbara ARI HTTP'
$ sudo ufw allow 8089/tcp  comment 'Verbara WSS WebRTC'
$ sudo ufw allow 20000:20200/udp comment 'Verbara RTP'
$ sudo ufw allow 80/tcp
$ sudo ufw allow 443/tcp     # opt — si terminás TLS en nginx
$ sudo ufw reload
$ sudo ufw status numbered    # validar
```

> 🔒 **AMI port 5038 NO se abre al mundo.** Si necesitás AMI para integraciones externas, restringilo a una IP específica:
> ```bash
> $ sudo ufw allow from 10.20.30.40 to any port 5038 comment 'AMI - integration server only'
> ```

### 2.2 firewalld (Rocky / Alma / Fedora)

```bash
$ sudo firewall-cmd --permanent --add-port=5060/udp
$ sudo firewall-cmd --permanent --add-port=5060/tcp
$ sudo firewall-cmd --permanent --add-port=5061/tcp
$ sudo firewall-cmd --permanent --add-port=8088/tcp
$ sudo firewall-cmd --permanent --add-port=8089/tcp
$ sudo firewall-cmd --permanent --add-port=20000-20200/udp
$ sudo firewall-cmd --permanent --add-service=http
$ sudo firewall-cmd --permanent --add-service=https   # opt
$ sudo firewall-cmd --reload
$ sudo firewall-cmd --list-all                         # validar
```

### 2.3 nftables (Debian 12 default)

Editar `/etc/nftables.conf` agregando dentro de tu chain `input` (asumiendo la default):

```nft
table inet filter {
    chain input {
        type filter hook input priority filter; policy drop;
        ct state established,related accept
        iif lo accept

        # Verbara — SIP signalling + ARI + RTP + Web
        udp dport 5060            accept comment "Verbara SIP UDP"
        tcp dport 5060            accept comment "Verbara SIP TCP"
        tcp dport 5061            accept comment "Verbara SIP TLS"
        tcp dport 8088            accept comment "Verbara ARI HTTP"
        tcp dport 8089            accept comment "Verbara WSS WebRTC"
        udp dport 20000-20200     accept comment "Verbara RTP"
        tcp dport 80              accept
        tcp dport 443             accept   # opt

        # SSH (no perder acceso al server)
        tcp dport 22              accept
    }
}
```

Recargar:
```bash
$ sudo nft -f /etc/nftables.conf
$ sudo systemctl enable --now nftables
$ sudo nft list ruleset             # validar
```

### 2.4 iptables crudo (Amazon Linux + otros)

```bash
$ sudo iptables -A INPUT -p udp --dport 5060 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 5060 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 5061 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 8088 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 8089 -j ACCEPT
$ sudo iptables -A INPUT -p udp --dport 20000:20200 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 80 -j ACCEPT
$ sudo iptables -A INPUT -p tcp --dport 443 -j ACCEPT   # opt

# Persistir (Amazon Linux usa iptables-services):
$ sudo dnf install -y iptables-services
$ sudo service iptables save
$ sudo systemctl enable iptables
```

## 3. NAT / Port-forwarding (Escenarios B y C)

Si tu escenario es B o C (ver §0), el firewall del host **no es suficiente** — necesitás que el router / cloud LB también deje pasar el tráfico hacia tu server.

### 3.1 On-prem detrás de un router doméstico/oficina (Escenario C)

#### MikroTik RouterOS

```
/ip firewall nat
add chain=dstnat protocol=udp dst-port=5060 action=dst-nat to-addresses=192.168.40.100 comment="Verbara SIP UDP"
add chain=dstnat protocol=tcp dst-port=5060 action=dst-nat to-addresses=192.168.40.100 comment="Verbara SIP TCP"
add chain=dstnat protocol=tcp dst-port=5061 action=dst-nat to-addresses=192.168.40.100 comment="Verbara SIP TLS"
add chain=dstnat protocol=tcp dst-port=8088 action=dst-nat to-addresses=192.168.40.100 comment="Verbara ARI"
add chain=dstnat protocol=tcp dst-port=8089 action=dst-nat to-addresses=192.168.40.100 comment="Verbara WSS"
add chain=dstnat protocol=udp dst-port=20000-20200 action=dst-nat to-addresses=192.168.40.100 comment="Verbara RTP"
add chain=dstnat protocol=tcp dst-port=80 action=dst-nat to-addresses=192.168.40.100
add chain=dstnat protocol=tcp dst-port=443 action=dst-nat to-addresses=192.168.40.100
```

> Si tu MikroTik ya tiene una regla DMZ apuntando al server (`/ip firewall nat add chain=dstnat action=dst-nat to-addresses=192.168.40.100`), eso ya cubre todos los puertos — no necesitás reglas individuales.

#### pfSense / OPNsense

UI: **Firewall → NAT → Port Forward → Add**

Crear una regla por puerto/rango:
- Interface: `WAN`
- Protocol: `UDP` o `TCP` según corresponda
- Destination: `WAN address`
- Destination port range: `5060` (o el rango RTP `20000-20200`)
- Redirect target IP: `192.168.40.100`
- Redirect target port: mismo que el origen
- Description: `Verbara SIP UDP` (o lo que aplique)

#### Ubiquiti EdgeOS / UniFi USG

```
configure
set port-forward rule 1 description 'Verbara SIP UDP'
set port-forward rule 1 forward-to address 192.168.40.100
set port-forward rule 1 forward-to port 5060
set port-forward rule 1 original-port 5060
set port-forward rule 1 protocol udp
set port-forward wan-interface eth0
# Repetir para cada puerto/rango
commit ; save
```

#### TP-Link / ASUS / consumer routers

UI varía pero todos tienen una sección "**Virtual Server**" o "**Port Forwarding**". Agregar una entrada por puerto/rango con:
- External port → Internal port → IP del server (`192.168.40.100`)
- Protocol: UDP / TCP / both según corresponda

> 💡 **Para evitar 8 reglas separadas**, casi todos estos routers soportan **DMZ host**: apuntar todo el tráfico no-matcheado hacia `192.168.40.100`. Más simple pero **menos seguro** — el server queda 100% expuesto. Sólo recomendable si el server tiene un firewall local bien configurado (paso 2).

### 3.2 Cloud VM (Escenario B)

#### AWS EC2 — Security Group

```bash
$ aws ec2 authorize-security-group-ingress --group-id sg-XXXX \
    --ip-permissions \
    'IpProtocol=udp,FromPort=5060,ToPort=5060,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara SIP UDP"}]' \
    'IpProtocol=tcp,FromPort=5060,ToPort=5060,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara SIP TCP"}]' \
    'IpProtocol=tcp,FromPort=5061,ToPort=5061,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara SIP TLS"}]' \
    'IpProtocol=tcp,FromPort=8088,ToPort=8088,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara ARI HTTP"}]' \
    'IpProtocol=tcp,FromPort=8089,ToPort=8089,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara WSS"}]' \
    'IpProtocol=udp,FromPort=20000,ToPort=20200,IpRanges=[{CidrIp=0.0.0.0/0,Description="Verbara RTP"}]' \
    'IpProtocol=tcp,FromPort=80,ToPort=80,IpRanges=[{CidrIp=0.0.0.0/0,Description="Web HTTP"}]' \
    'IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0,Description="Web HTTPS"}]'
```

> 🔒 **Buena práctica:** restringir 8088/8089/5038 a las IPs de tus oficinas y trunk providers — no hay razón para que estén accesibles al mundo. Reemplazar `CidrIp=0.0.0.0/0` con el CIDR de las IPs autorizadas.

#### Azure — Network Security Group

```bash
$ az network nsg rule create --resource-group myRG --nsg-name myNSG \
    --name verbara-sip-udp --priority 1000 \
    --direction Inbound --access Allow --protocol Udp \
    --source-address-prefixes '*' --source-port-ranges '*' \
    --destination-address-prefixes '*' --destination-port-ranges 5060

# Repetir para 5060/tcp, 5061/tcp, 8088, 8089, 80, 443, 20000-20200/udp
```

#### GCP — firewall rule

```bash
$ gcloud compute firewall-rules create verbara-sip \
    --direction=INGRESS --action=ALLOW \
    --rules=udp:5060,tcp:5060,tcp:5061,tcp:8088,tcp:8089,tcp:80,tcp:443 \
    --source-ranges=0.0.0.0/0

$ gcloud compute firewall-rules create verbara-rtp \
    --direction=INGRESS --action=ALLOW \
    --rules=udp:20000-20200 \
    --source-ranges=0.0.0.0/0
```

#### Hetzner Cloud — firewall

UI: **Cloud Console → Firewalls → Create Firewall → Inbound Rules**. Agregar 8 reglas equivalentes a las anteriores y attachear al servidor.

### 3.3 CGNAT (Escenario D) — el caso problemático

Si tu IP pública cae en `100.64.0.0/10`, tu ISP te tiene detrás de NAT. Esto significa que **no hay forma de hacer port-forwarding desde internet a tu server** — el NAT del ISP es opaco.

Opciones:

1. **Pedir IP pública dedicada al ISP.** Muchos ISPs lo ofrecen como add-on de pago (Telmex, Movistar, Claro, etc.).
2. **Usar un VPS-proxy SIP intermedio.** Levantás un Kamailio/Asterisk en un VPS público (DigitalOcean, Hetzner, Linode — desde $5/mes) y configurás un trunk SIP entre él y tu Asterisk on-prem. El VPS es la cara visible al mundo.
3. **Trunk SIP solo outbound.** Si tu negocio no necesita recibir llamadas (sólo outbound desde agentes), igual funciona — Asterisk inicia las conexiones hacia el trunk, y el response viene por la misma conexión NAT (siempre que sea TCP/TLS).
4. **WebRTC-only para inbound.** Los agentes pueden recibir llamadas WebRTC siempre que el origen sea un browser visitante de tu sitio web (Click-to-Call) — eso pasa por HTTPS/WSS y no requiere port-forwarding inbound.

## 4. DNS records (recomendado)

Para acceder al Web con un nombre legible (no `https://200.118.42.61`) y para que TLS funcione, configurá:

| Tipo | Nombre | Valor | TTL |
|---|---|---|---|
| A | `verbara.tu-dominio.com` | `{tu-IP-pública}` | 300 |
| A | `pbx.tu-dominio.com` | `{tu-IP-pública}` | 300 |

> Si querés separar la API del Web (recomendado para producción):
> | Tipo | Nombre | Valor | TTL |
> |---|---|---|---|
> | A | `app.tu-dominio.com` | `{tu-IP-pública}` | 300 |
> | A | `api.tu-dominio.com` | `{tu-IP-pública}` | 300 |
> | A | `pbx.tu-dominio.com` | `{tu-IP-pública}` | 300 |

Validar:
```bash
$ dig +short verbara.tu-dominio.com    # → debe devolver tu IP pública
```

## 5. TLS Let's Encrypt (opcional pero recomendado)

> Sin TLS, los browsers de los agentes NO van a permitir el micrófono por WebRTC (excepto en `localhost`). Para producción **es obligatorio**.

### 5.1 Standalone certbot (más simple)

```bash
$ sudo apt install -y certbot                      # Debian/Ubuntu
$ sudo dnf install -y certbot                      # Rocky/Alma

# Detener cualquier cosa en port 80 (incluyendo Verbara si ya corre)
$ sudo systemctl stop nginx apache2 2>/dev/null
$ docker stop verbara-web 2>/dev/null

# Pedir el certificado (el standalone arranca un mini-server temporal en 80)
$ sudo certbot certonly --standalone \
    -d verbara.tu-dominio.com -d pbx.tu-dominio.com \
    --email tu@email.com --agree-tos --no-eff-email

# Los certs quedan en /etc/letsencrypt/live/verbara.tu-dominio.com/
$ sudo ls /etc/letsencrypt/live/verbara.tu-dominio.com/
fullchain.pem  privkey.pem  chain.pem  cert.pem
```

**Renovación automática** (certbot ya instala un timer):
```bash
$ sudo systemctl status certbot.timer        # debe estar active
$ sudo certbot renew --dry-run               # validar que la renovación funciona
```

En `02-arranque-stack.md` vas a configurar el `.env.reference-smb` para que el Web nginx use estos certs.

### 5.2 Wildcard cert (multi-subdominio)

Si querés un solo cert que cubra `*.tu-dominio.com`, certbot requiere el plugin DNS de tu provider (cloudflare/digitalocean/route53/etc.):

```bash
$ sudo apt install -y python3-certbot-dns-cloudflare
$ sudo certbot certonly --dns-cloudflare \
    --dns-cloudflare-credentials /root/.cloudflare.ini \
    -d "*.tu-dominio.com" -d "tu-dominio.com" \
    --email tu@email.com --agree-tos
```

## 6. Validación final de pre-requisitos

Antes de pasar al manual 02, validá que **todo lo siguiente** dé verde:

```bash
# 1. Docker daemon respondiendo, usuario en grupo docker
$ docker info | head -5
$ docker run --rm hello-world

# 2. Docker compose plugin v2.20+
$ docker compose version

# 3. Puertos del host libres (no hay otro PBX corriendo)
$ ss -tln '( sport = :5060 or sport = :8088 or sport = :8089 )'
$ ss -uln '( sport = :5060 )'
# ↑ ambas queries deben devolver vacío

# 4. Firewall del host abrió los puertos
$ sudo ufw status                       # o firewall-cmd --list-all o nft list ruleset

# 5. Port-forwarding (si on-prem detrás de NAT)
# Desde OTRA máquina en internet (móvil con 4G, AWS bastion, etc.):
$ nc -uvz {tu-IP-pública} 5060 < /dev/null
# Esperado: "Connection to {tu-IP-pública} 5060 port [udp/sip] succeeded!"
# (Esto requiere que Asterisk ya esté corriendo o algún listener.
#  Por ahora basta con que el packet llegue al server — chequeable con
#  `sudo tcpdump -i any port 5060` corriendo en el server mientras pegás nc.)

# 6. DNS resolviendo (si configuraste records)
$ dig +short verbara.tu-dominio.com
# → debe devolver tu IP pública
```

Si **todos** dan verde, estás listo para [02-arranque-stack.md](02-arranque-stack.md).

Si alguno falla, revisá la sección correspondiente arriba o consultá [99-troubleshooting.md](99-troubleshooting.md).

## Próximo paso

→ [02-arranque-stack.md](02-arranque-stack.md) — clonar el repo, editar `.env`, ejecutar `quickstart-smb.sh`, esperar que todos los servicios estén `healthy`.
