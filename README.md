# Fjern — skærmdeling og fjernstyring (hjælpeværktøj) · h4xtor-help

**2 EXE:**
- `FjernAdmin.exe` — til dig (hjælper): tast rum + kode, Forbind — fuld skærm, mus + tastatur, tale, webcam, skærmbillede.
- `FjernUser.exe` — til din ven: åbn, **rum + kode vises med det samme**, admin kan forbinde.
- `FjernRelay.exe` — relay-server (kør på Oracle VPS under Mono).

Forbindelsen er **krypteret ende-til-ende** (ECDH P-256 + AES-256-CBC + HMAC-SHA256).
Relayet ser kun krypterede pakker — aldrig skærminhold, chat, filer eller lyd.

## Krav
- Windows 7/8/10/11 (begge EXE kører som almindelig bruger)
- .NET Framework 4.x (følger med Windows)
- Ingen anden installation

## Byg (valgfri — EXE'er ligger færdige i repoet)
```bat
build.cmd   → FjernAdmin.exe + FjernUser.exe + FjernRelay.exe
```
Kilder skal gemmes som UTF-8 **med BOM** — ellers læser csc æ/ø/å som ANSI.

## VPS-relæ (Oracle, Ubuntu)
```bash
scp FjernRelay.exe ubuntu@<vps>:~/fjern/
sudo apt-get update && sudo apt-get install -y mono-runtime

# /etc/systemd/system/fjern-relay.service
# ExecStart=/usr/bin/mono /home/ubuntu/fjern/FjernRelay.exe 6767
# Restart=always
sudo systemctl daemon-reload && sudo systemctl enable --now fjern-relay
```

**Firewall:**
1. Oracle-konsol: Networking → VCN → Security List → Ingress: `0.0.0.0/0`, TCP, port `6767`.
2. Ubuntu: `sudo ufw allow 6767/tcp`.

## Sådan forbinder I
1. Vennen kører `FjernUser.exe` → rum + kode står i venstre bjælke.
2. Han giver dig de to koder (SMS/chat).
3. Du kører `FjernAdmin.exe` → fanen "Styr en vens pc":
   - Relay-server = din VPS-adresse, port 6767
   - Rum-ID + kode fra vennen
   - **Forbind** → du ser hans skærm nu.
4. Nedenfor i vinduet kan du styre:
   - mus + tastatur (F11 = fuld skærm)
   - **Tale** — voicecall begge veje + webcam
   - **Skud** — screenshot sendt tilbage og gemt på din PC
   - **Chat / Filer / Klip** — begge retninger

**Indstillinger (venner):** Indstillinger-relayserver kan sættes til
din VPS-IP → vennens `FjernUser.exe` starter deling automatisk ved åbning.

## Test
```bat
FjernUser.exe selftest              → ALT OK
FjernRelay.exe 47311
FjernUser.exe selftest full 47311   → FULD TEST OK
```

## Direkte LAN (uden VPS)
Hvis begge er på samme netværk, lad Relay-serveren stå tom:
- `FjernUser.exe` deler direkte via LAN (lytter på port 6768).
- `FjernAdmin.exe`: "Direkte: din vens IP" (vennens LAN-IP) og "Direkte: port" = 6768.

Over internettet: brug relayet (se VPS-afsnittet ovenfor).
