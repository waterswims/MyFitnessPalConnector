# MyFitnessPal Connector for Claude

A small ASP.NET Core **remote MCP server** that lets Claude (including the Android app) read and write your
MyFitnessPal diary. It runs on AWS Lambda behind API Gateway at `myfitnesspal.jonathonwaterford.com`, and is added to Claude as a **custom connector**.

```
Claude app ──MCP over HTTPS──> API Gateway ──> Lambda /mcp/{secret} ──(session cookie)──> www.myfitnesspal.com/api/...
```

## Tools

| Tool | What it does |
|---|---|
| `get_diary` | A day's entries with nutrition, totals, goals and what's remaining |
| `search_foods` | Foods you logged in the last 14 days or created yourself, including how you last logged them |
| `log_food` | Adds a food from `search_foods` to a meal |
| `quick_add` | Logs calories and macros directly (for foods `search_foods` can't find) |
| `delete_diary_entry` | Removes a diary entry |
| `get_weight_history` | Weigh-ins (kg) for the last N days |
| `log_weight` | Records a weigh-in (kg, lb or st) |

## Things to know

- **MyFitnessPal has no public API.** This calls the same private routes the MFP website uses (verified on
  2026-11-13). It goes against MFP's terms of service, and it can break whenever MFP changes something.
- **The MFP food database can't be searched.** MFP removed public search from the website, so Claude uses foods you've
  logged recently or created, and quick-adds estimates for anything else.
- **Security:** there's no login. The secret in the URL path is the credential, so anyone with the full URL can read and
  write your diary. To revoke access, change `McpPathSecret`. You can also set `AllowedSourceCidrs` (see [DEPLOY.md](docs/DEPLOY.md)).
- **MFP session:** your browser's `__Secure-next-auth.session-token` cookie. MFP renews it on use, and the server saves
  the renewed value, so you only need to paste it again after 30 days without use.

## Code

- [Program.cs](src/MfpConnector.Api/Program.cs): hosting, the secret path, the optional IP allowlist
- [Mfp.cs](src/MfpConnector.Api/Mfp.cs): calls to MFP, and the session cookie (from SSM, saved back when MFP renews it)
- [MfpTools.cs](src/MfpConnector.Api/MfpTools.cs): the MCP tools

## Run locally

```bash
cd src/MfpConnector.Api
dotnet user-secrets set McpPathSecret "$(openssl rand -hex 32)"
dotnet user-secrets set MfpCookie '<value of __Secure-next-auth.session-token>'
dotnet run    # MCP endpoint: http://localhost:5080/mcp/<secret>
```

You can try it with `npx @modelcontextprotocol/inspector` (Streamable HTTP, URL above).

## Test

```bash
dotnet test                       # offline: only the secret path answers, and the tools are registered

# Against your real account (read-only):
echo 'MFP_COOKIE=<cookie value>' > .env    # git-ignored
MFP_LIVE_COOKIE="$(sed -n 's/^MFP_COOKIE=//p' .env)" dotnet test --filter Category=Live
# Add MFP_LIVE_WRITES=1 to also log food, a quick add and a weight on 2020-01-01 (only if that day is empty),
# then delete them again.
```

## Deploy

`./deploy.sh` uploads a new build to the existing Lambda. [docs/DEPLOY.md](docs/DEPLOY.md) lists the AWS resources, and covers
maintenance and teardown.
