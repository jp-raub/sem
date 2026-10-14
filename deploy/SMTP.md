# SMTP authentication and sender configuration

This is the English reference. [Japanese version](SMTP.ja.md).

SMTP authentication and the message sender are independent settings:

| Setting | Purpose |
| --- | --- |
| SMTP_USERNAME | SMTP authentication ID; it need not be an email address or match the sender |
| SMTP_PASSWORD | Password for that authentication ID; keep private |
| SMTP_FROM | Sender address used in the message From header |

The sender for this deployment is `sem-app-notification@s.raub.jp`. Change it later by editing SMTP_FROM in the existing private `.env` and recreating the service. Do not replace an existing `.env` with `.env.example`.

```dotenv
SMTP_FROM=sem-app-notification@s.raub.jp
```

Compose passes SMTP_FROM to Smtp__From. For compatibility, `${SMTP_FROM:-misc@raub.jp}` falls back to the previous sender when SMTP_FROM is absent or empty. No authentication-ID-to-sender matching requirement is added by the app. The SMTP server must authorize the chosen sender and may impose its own sender restrictions or rewriting. Configure SPF/DKIM/DMARC as appropriate for the sender domain at the mail service.

## Apply to the existing DXP deployment

For a deployment made from the old delivery ZIP, change only this environment line in the existing deploy/compose.yaml:

```yaml
      Smtp__From: ${SMTP_FROM:-misc@raub.jp}
```

Add/update SMTP_FROM in the existing `.env`, keeping SMTP_USERNAME and SMTP_PASSWORD at the values issued by the new SMTP server. Preserve all other settings, especially the public URL, trusted proxy, data volume and Push keys.

```sh
cd /volume2/Docker/sem-dxp-delivery/deploy
docker compose -p sem-external-test --env-file .env \
  -f compose.yaml up -d --no-deps --force-recreate --no-build --pull never sem
docker compose -p sem-external-test --env-file .env -f compose.yaml ps sem
```

This causes a brief interruption but does not delete the data volume. A restart alone does not apply changed environment variables. The application already reads Smtp:Username and Smtp:From separately, so this Compose configuration change does not require an image rebuild. Do not use restore-check.override.yaml for normal operation and do not run down -v.

Log in as the global administrator, select the notification-management test-email button, and verify actual receipt and the From address. Where available, check the new SMTP server logs to confirm the delivery route. A queued/sent status is not proof of recipient delivery or preservation of the From header by the SMTP server.

This change concerns email only. Push__Subject (the VAPID contact) and VAPID keys are unchanged. Do not paste `.env`, expanded Compose configuration or SMTP credentials into chat or Git.
