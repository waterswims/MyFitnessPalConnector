# Deploying to AWS Lambda (console)

What you'll end up with: one Lambda function behind a Function URL, plus one SSM parameter that holds the MFP cookie.
Use the same region throughout.

## 1. Build the zip

Requires the `zip` utility (`sudo pacman -S zip`).

```bash
dotnet tool restore
cd src/MfpConnector.Api
dotnet lambda package -o ../../artifacts/mfp-connector.zip
```

## 2. Store the MFP cookie

1. In a desktop browser, sign in at myfitnesspal.com. Open DevTools → Application → Cookies →
   `https://www.myfitnesspal.com` and copy the value of `__Secure-next-auth.session-token`.
2. Console → **Systems Manager → Parameter Store → Create parameter**:
   - **Name:** `/mfp-connector/mfp-session`
   - **Type:** SecureString, with the default `alias/aws/ssm` key
   - **Value:** the cookie

## 3. IAM role

Console → **IAM → Roles → Create role** → trusted entity **Lambda**.

- Attach `AWSLambdaBasicExecutionRole`.
- Add this inline policy, replacing `REGION` and `ACCOUNT_ID`. The server writes the renewed cookie back, which is why it needs `PutParameter`.

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["ssm:GetParameter", "ssm:PutParameter"],
    "Resource": "arn:aws:ssm:REGION:ACCOUNT_ID:parameter/mfp-connector/mfp-session"
  }]
}
```

## 4. Lambda function

Console → **Lambda → Create function**:
- **Runtime:** .NET 10
- **Architecture:** arm64
- **Execution role:** the role from step 3

Then:

1. **Code → Upload from → .zip file** → `artifacts/mfp-connector.zip`.
2. **Runtime settings → Handler:** `MfpConnector.Api`.
3. **Configuration → General:** 512 MB, 30 s timeout.
4. **Configuration → Function URL → Create:** auth type **NONE**.
5. **Configuration → Environment variables:**

   | Key | Value |
   |---|---|
   | `McpPathSecret` | output of `openssl rand -hex 32` |
   | `MfpCookieParameter` | `/mfp-connector/mfp-session` |
   | `UserTimeZone` | e.g. `Europe/London` |
   | `AllowedSourceCidrs` | optional; see below |

## 5. Add the connector to Claude

1. On claude.ai (web), go to **Settings → Connectors → Add custom connector**.
2. Set the URL to `https://<function-url>/mcp/<McpPathSecret>`. Leave the OAuth fields empty.
3. In the Android app, enable the connector in a chat. Connectors added on the web sync to mobile.

Try: "What have I eaten today?", "Log my usual porridge for breakfast", "Log my weight as 82.4 kg".

## Optional: restrict source IPs

Set `AllowedSourceCidrs` to a comma-separated list of CIDR ranges, e.g. the egress IP addresses Anthropic publishes
for MCP connector traffic (look up the current list in Anthropic's docs). Requests from any other address get a 404,
so a leaked URL alone isn't enough. Leave it unset to allow any address.

## Maintenance

- **Session expired** (Claude will tell you): copy a fresh cookie (step 2.1) and update the SSM parameter's value.
  The next tool call picks it up.
- **Updating the code:** rebuild the zip, then use **Code → Upload from → .zip file**.
- **Revoking access:** change `McpPathSecret`, then update the connector URL in Claude.

## Troubleshooting

- **"Cloudflare protection blocked this server":** MFP is challenging requests from AWS IP addresses. Requests from a home
  connection weren't challenged during testing. Your options are to send MFP calls through a proxy, or to host elsewhere.
- **No .NET 10 runtime in your region:** add `"msbuild-parameters": "--self-contained true"` to
  `aws-lambda-tools-defaults.json` and `<AssemblyName>bootstrap</AssemblyName>` to the `.csproj`. Then use the
  `provided.al2023` runtime with handler `bootstrap`.
- **Logs:** Lambda → Monitor → View CloudWatch logs.
