# Encrypting the data protection keys

Sign-in cookies, bearer tokens, two-factor steps and emailed links are protected with data protection keys kept in
the database. Stored as plain XML, anyone who reads the database or a backup of it can forge a sign-in cookie, so in
Production the server warns at startup until they are encrypted with a certificate: a `.pfx` file with an RSA
private key.

## Create the certificate

With PowerShell:

```powershell
$certificate = New-SelfSignedCertificate -Subject "CN=StealthDesk keys" -KeyAlgorithm RSA -KeyLength 3072 `
  -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
Export-PfxCertificate -Cert $certificate -FilePath stealthdesk-keys.pfx -Password (Read-Host -AsSecureString "Password")
Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
```

or with OpenSSL:

```bash
openssl req -x509 -newkey rsa:3072 -nodes -keyout keys.pem -out keys.crt -days 3650 -subj "/CN=StealthDesk keys"
openssl pkcs12 -export -inkey keys.pem -in keys.crt -out stealthdesk-keys.pfx
rm keys.pem keys.crt
```

## Turn encryption on

Turn on `KeyProtection:EncryptKeys` and point `KeyProtection:Certificate:Path` at the file (or put its base64
contents in `KeyProtection:Certificate:Base64`), with its password in `KeyProtection:Certificate:Password`. Keep the
file and its password out of the database backups, or encrypting gains nothing. The server refuses to start when the
certificate is missing, can't be read or has no private key. Keys written before stay readable; new ones are
encrypted. The certificate's expiry date doesn't matter.

With [Docker Compose](docker-compose.md), the certificate goes in a `docker-compose/certificates` folder you create, or in a Docker secret.

## Keeping it safe

- **Keep a copy of the `.pfx` and its password off the server**, in a password manager or a vault. Lost with the
  server, the keys in a database backup can't be read again: everyone signs in again, and emailed links stop working.
- **Keep them apart from the database backups.** Together, they let anyone who has them forge a sign-in.
- **Let only the server read it.** The container runs as the image's `app` user, uid 1654, so on Linux give the file
  to that user and nobody else: `sudo chown 1654:1654 certificates/stealthdesk-keys.pfx` and
  `sudo chmod 400 certificates/stealthdesk-keys.pfx`. Made readable only by your own account instead, the server can't
  read it and doesn't start. The password belongs in `.env` (itself `chmod 600`) or in a Docker secret, never in a
  file under version control.
- **Rotate it** when someone who had it leaves, or if it may have leaked, as below.

## Rotating the certificate

To rotate the certificate, create a new one, make it the `Certificate`, and move the old one to
`KeyProtection:PreviousCertificates:0` (`:Path`, `:Base64`, `:Password`, like the current one). Previous certificates
only read the keys they encrypted, so nobody is signed out. Keys last 90 days, and sessions made with them up to
30 more, so keep the old certificate for at least four months; dropped sooner, those sessions and links stop
working and their users sign in again. Turning encryption off works the same way: keep the certificate as a previous
one until its keys are gone.
