# Setting up Microsoft credentials

Drive Migrator signs in to Microsoft (OneDrive, Outlook mail, calendar and contacts) with an app registration
that you create in Microsoft Entra ID. One registration can serve both personal Microsoft accounts and
work/school accounts.

## 1. Register the app

1. Open the [Microsoft Entra admin center](https://entra.microsoft.com/) → **Identity → Applications → App registrations → New registration**.
   You need an Entra tenant for this. Work/school users already have one, and anyone with a personal account can
   create a free tenant.
2. **Name**: e.g. "Drive Migrator".
3. **Supported account types**: pick one of these:
   - **Any Entra ID tenant + personal Microsoft accounts**: works for everyone. Leave the *Tenant* setting empty.
   - **Single tenant**: only your organization. Enter your tenant ID or domain in the *Tenant* setting.
4. **Redirect URI**: platform **Public client/native (mobile & desktop)**, value `http://localhost`.
5. Select **Register**, then copy the **Application (client) ID** from the Overview page.

## 2. Add API permissions

Under **API permissions → Add a permission → Microsoft Graph → Delegated permissions**, add:

| Permission | Used for |
|---|---|
| `User.Read` | Identifying the signed-in account |
| `Files.ReadWrite.All` | OneDrive |
| `Mail.ReadWrite` | Outlook mail |
| `Calendars.ReadWrite` | Calendars |
| `Contacts.ReadWrite` | Contacts |
| `offline_access` | Staying signed in |

Work/school tenants that don't allow user consent need an administrator to select **Grant admin consent**.
Otherwise sign-in fails with an "admin approval required" message.

## 3. Enter it in Drive Migrator

Open **Settings → Credentials → Microsoft**, paste the Application (client) ID, and optionally set **Tenant**:

| Tenant value | Who can sign in |
|---|---|
| *(empty)* / `common` | Personal and work/school accounts |
| `organizations` | Any work/school account |
| `consumers` | Personal accounts only |
| tenant ID or domain | Only that organization |

Select **Save**, then connect accounts from **Settings → Accounts → Add Microsoft account**. Sign-in opens in your
default browser and returns to a temporary `http://localhost` listener.

If you change the client ID later, connected Microsoft accounts need to be re-authorized.
