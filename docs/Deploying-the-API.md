# Deploying the API to production

Production is IIS on **QUADITMAPP001**, site and app pool **sosa-fam-api.nuvol9.com**, folder `D:\Inetpub\sosa-fam-api.nuvol9.com`, listening on `http://localhost:7170`. Cloudflare publishes it as `https://sosa-fam-api.nuvol9.com`. The database is `SOS_FAMILY` on SOLITMDB001, reached with the `sosa-fam-api` login, which is in the `sosafam_api` role only.

Both scripts use PowerShell remoting with your own Windows sign-in. Nothing is stored for the server.

## One-time setup

1. Install the **.NET 8 Hosting Bundle** on the server if it isn't there yet. Solasta's API already needs it.
2. Preview, then create the app pool, site and folders:
   ```powershell
   .\deploy\Publish-SosaFamApiProd.ps1 -InitializeSite -WhatIf
   .\deploy\Publish-SosaFamApiProd.ps1 -InitializeSite
   ```
3. **Secrets (you run these, never Claude).** Each one asks for the value at a hidden prompt and stores it as an environment variable on the app pool:
   ```powershell
   .\deploy\Set-SosaFamApiProdSecret.ps1 -Name ConnectionStrings__SosaFam
   .\deploy\Set-SosaFamApiProdSecret.ps1 -Name Jwt__SigningKey -Generate
   ```
   - `ConnectionStrings__SosaFam`: `Server=SOLITMDB001;Database=SOS_FAMILY;User Id=sosa-fam-api;Password=<password>;TrustServerCertificate=True`
   - `Jwt__SigningKey`: `-Generate` makes a random 64-byte key that nobody sees. Replacing it later signs everyone out.
   - `-List` shows which names are set, without their values. `-Remove` deletes one.
4. **Cloudflare:** in the tunnel that serves api.mysolasta.com, add the public hostname `sosa-fam-api.nuvol9.com` pointing at `http://localhost:7170`.

## Each release

1. The change is merged to `main`, and the database migrations it needs are already on Prod.
2. Preview:
   ```powershell
   .\deploy\Publish-SosaFamApiProd.ps1 -WhatIf
   ```
   It checks that `main` is in sync, runs the tests, publishes, and checks that the site, the app pool and both secrets exist. It changes nothing.
3. Run the same command without `-WhatIf`. Type `QUADITMAPP001/sosa-fam-api.nuvol9.com` at the prompt.

   The script puts up `app_offline.htm` and backs up the current files (keeping the newest 5). Then it copies the new files, keeping `logs\`, recycles the pool, and checks that `http://localhost:7170/health` answers 200.
4. If the new version misbehaves, run `.\deploy\Publish-SosaFamApiProd.ps1 -Rollback` to put the newest backup back.

SignalR (`/hubs/household`) needs the **WebSocket Protocol** feature installed in IIS, and WebSockets turned on in Cloudflare (they are on by default).

## Rules for Claude

- Publish only through a Remote Control session on the person's PC.
- Never run `Set-SosaFamApiProdSecret.ps1`, never ask for or handle a secret, and never print app pool environment values. `-List` shows names only.
- Show the exact command and the `-WhatIf` output, then wait for an explicit go. That go allows `-ConfirmTarget 'QUADITMAPP001/sosa-fam-api.nuvol9.com'`.
- On a failure, stop and report. Don't retry. Roll back only with the person's go.
