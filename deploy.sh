#!/usr/bin/env bash
# Builds the app and uploads it to the existing Lambda function (see docs/DEPLOY.md).
set -euo pipefail
cd "$(dirname "$0")"

export AWS_REGION="${AWS_REGION:-eu-west-2}"
FUNCTION=mfp-connector
OUT=artifacts/publish
ZIP=artifacts/mfp-connector.zip

rm -rf "$OUT" "$ZIP"
dotnet publish src/MfpConnector.Api -c Release -r linux-arm64 --self-contained false -o "$OUT" --nologo -v quiet

# Zip with Python so the `zip` utility isn't needed.
python3 -I - "$OUT" "$ZIP" <<'EOF'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            path = os.path.join(root, f)
            info = zipfile.ZipInfo.from_file(path, os.path.relpath(path, src))
            info.external_attr = 0o755 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as fh:
                z.writestr(info, fh.read())
EOF

aws lambda update-function-code --function-name "$FUNCTION" --zip-file "fileb://$ZIP" --query LastUpdateStatus --output text
aws lambda wait function-updated-v2 --function-name "$FUNCTION"
echo "Deployed $(du -h "$ZIP" | cut -f1) to $FUNCTION"
