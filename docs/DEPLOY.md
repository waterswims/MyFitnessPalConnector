# Deployment

Live at **`https://myfitnesspal.jonathonwaterford.com/mcp/<McpPathSecret>`** (AWS account 032621504602, region eu-west-2).
The secret is in `MCP_PATH_SECRET` in your local `.env`, and in the Lambda's environment variables.

## Resources

| Resource | Name |
|---|---|
| Lambda (.NET 10, arm64, 512 MB, 30 s) | `mfp-connector`; env vars `McpPathSecret`, `MfpCookieParameter`, `UserTimeZone` |
| IAM role | `mfp-connector-lambda`: basic logging, plus `ssm:GetParameter`/`PutParameter` on the cookie parameter |
| SSM parameter (SecureString) | `/mfp-connector/mfp-session`: the MFP session cookie; the Lambda saves renewed values back |
| API Gateway HTTP API | `mfp-connector` (`a0i6uhabjd`): `$default` route → Lambda; the default `execute-api` endpoint is disabled |
| API Gateway custom domain | `myfitnesspal.jonathonwaterford.com` (regional, TLS 1.2) |
| ACM certificate | `myfitnesspal.jonathonwaterford.com` (DNS-validated, auto-renews) |
| Route 53 (`jonathonwaterford.com`) | `A` alias → API Gateway domain; `CNAME` for ACM validation |

API Gateway is used instead of a Lambda Function URL because Function URLs can't have custom domains.

## Add the connector to Claude

1. On claude.ai (web), go to **Settings → Connectors → Add custom connector**.
2. Set the URL to `https://myfitnesspal.jonathonwaterford.com/mcp/<MCP_PATH_SECRET from .env>`. Leave the OAuth fields empty.
3. In the Android app, enable the connector in a chat. Connectors added on the web sync to mobile.

## Updating the code

```bash
aws login --region eu-west-2   # if the session has expired
./deploy.sh                    # dotnet publish → zip → aws lambda update-function-code
```

## Maintenance

- **MFP session expired** (Claude will tell you): sign in at myfitnesspal.com, copy the
  `__Secure-next-auth.session-token` cookie (DevTools → Application → Cookies), then:
  `aws ssm put-parameter --region eu-west-2 --name /mfp-connector/mfp-session --type SecureString --overwrite --value '<cookie>'`
- **Revoking access:** set a new `McpPathSecret` on the Lambda (`openssl rand -hex 32`), then update the connector URL in Claude.
- **Restricting source IPs (optional):** add a Lambda env var `AllowedSourceCidrs` with comma-separated CIDR ranges, e.g.
  the egress IP addresses Anthropic publishes for MCP connector traffic (check Anthropic's docs for the current list).
- **Logs:** `aws logs tail /aws/lambda/mfp-connector --region eu-west-2 --since 1h`

## Tearing it down

```bash
export AWS_REGION=eu-west-2
aws apigatewayv2 delete-domain-name --domain-name myfitnesspal.jonathonwaterford.com
aws apigatewayv2 delete-api --api-id a0i6uhabjd
aws lambda delete-function --function-name mfp-connector
aws iam delete-role-policy --role-name mfp-connector-lambda --policy-name mfp-session-parameter
aws iam detach-role-policy --role-name mfp-connector-lambda --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole
aws iam delete-role --role-name mfp-connector-lambda
aws ssm delete-parameter --name /mfp-connector/mfp-session
aws acm delete-certificate --certificate-arn arn:aws:acm:eu-west-2:032621504602:certificate/1435c42e-30fc-46c8-b414-281789560030
aws logs delete-log-group --log-group-name /aws/lambda/mfp-connector
# Then delete the myfitnesspal A record and the ACM validation CNAME from the jonathonwaterford.com hosted zone.
```
