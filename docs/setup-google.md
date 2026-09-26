# Setting up Google credentials

Drive Migrator signs in to Google with an OAuth client that you create in your own Google Cloud project.
You do this once; every Google account you connect afterwards uses the same client.

## 1. Create a project and enable the APIs

1. Open the [Google Cloud console](https://console.cloud.google.com/) and create a project (or pick an existing one).
2. Under **APIs & Services → Library**, enable:
   - Google Drive API
   - Gmail API
   - Google Calendar API
   - People API

## 2. Configure the consent screen

Under **Google Auth Platform** (formerly *OAuth consent screen*):

1. **Branding**: set an app name (e.g. "Drive Migrator") and support email.
2. **Audience**:
   - **Internal**: available when the project belongs to a Google Workspace organization. Only users of that
     organization can sign in, with no user cap and no verification. This is the best choice for company migrations.
   - **External**: for personal Gmail accounts. While the app is in *Testing* status, add each Google account
     that will sign in under **Test users** (up to 100).
3. **Data access**: add these scopes:
   - `openid`, `.../auth/userinfo.email`, `.../auth/userinfo.profile`
   - `https://www.googleapis.com/auth/drive`
   - `https://www.googleapis.com/auth/gmail.modify`
   - `https://www.googleapis.com/auth/calendar`
   - `https://www.googleapis.com/auth/contacts`

> **External apps in Testing status:** Google expires refresh tokens after **7 days**, so accounts show
> *Needs re-authorization* about a week after connecting. Re-authorize them in Settings, or publish the app.
> Publishing an app that uses Drive and Gmail scopes to the general public requires Google's verification.

## 3. Create the OAuth client

1. **Clients → Create client** (or *Credentials → Create credentials → OAuth client ID*).
2. Application type: **Desktop app**. Name it anything.
3. Copy the **Client ID** and **Client secret**.

Desktop clients sign in through your browser and redirect back to a temporary `http://127.0.0.1:<port>` listener,
which Google allows for Desktop clients without extra configuration.

## 4. Enter them in Drive Migrator

Open **Settings → Credentials → Google**, paste the client ID and secret, and select **Save**.
Then connect accounts from **Settings → Accounts → Add Google account**.

If you change the client ID later, connected Google accounts need to be re-authorized, because their saved
sign-ins belong to the old client.
