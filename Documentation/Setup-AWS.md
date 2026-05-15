# Setup — AWS S3 + CloudFront

One-time setup for the Content Repo upload pipeline using the bundled `AwsUploadProvider`. Replace `your-bucket-name`, `your-account-id`, and `eu-central-1` with your values throughout.

---

## 1. Create the S3 bucket

```bash
aws s3api create-bucket \
  --bucket your-bucket-name \
  --region eu-central-1 \
  --create-bucket-configuration LocationConstraint=eu-central-1
```

Block all public access (CloudFront will read it via Origin Access Control, not the public web):

```bash
aws s3api put-public-access-block \
  --bucket your-bucket-name \
  --public-access-block-configuration \
  "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"
```

## 2. Create the CloudFront distribution

Create an Origin Access Control (OAC) in S3-signing mode:

```bash
aws cloudfront create-origin-access-control \
  --origin-access-control-config \
  Name=content-repo-oac,SigningProtocol=sigv4,SigningBehavior=always,OriginAccessControlOriginType=s3
```

Note the returned `Id` — you will paste it into the CloudFront distribution config below.

Create the distribution (paste the OAC `Id` and bucket name into the JSON):

```bash
cat > /tmp/cf-config.json <<'EOF'
{
  "CallerReference": "content-repo-init",
  "Comment": "Content Repo CDN",
  "Enabled": true,
  "Origins": {
    "Quantity": 1,
    "Items": [{
      "Id": "s3-content",
      "DomainName": "your-bucket-name.s3.eu-central-1.amazonaws.com",
      "S3OriginConfig": { "OriginAccessIdentity": "" },
      "OriginAccessControlId": "PASTE_OAC_ID_HERE"
    }]
  },
  "DefaultCacheBehavior": {
    "TargetOriginId": "s3-content",
    "ViewerProtocolPolicy": "redirect-to-https",
    "AllowedMethods": { "Quantity": 2, "Items": ["GET","HEAD"], "CachedMethods": { "Quantity": 2, "Items": ["GET","HEAD"] } },
    "CachePolicyId": "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }
}
EOF

aws cloudfront create-distribution --distribution-config file:///tmp/cf-config.json
```

Record the returned `DistributionId` and `DomainName` (e.g. `d111111abcdef8.cloudfront.net`) — both go into Project Settings.

Allow CloudFront to read from the bucket:

```bash
cat > /tmp/bucket-policy.json <<'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "AllowCloudFrontServicePrincipalReadOnly",
    "Effect": "Allow",
    "Principal": { "Service": "cloudfront.amazonaws.com" },
    "Action": "s3:GetObject",
    "Resource": "arn:aws:s3:::your-bucket-name/*",
    "Condition": {
      "StringEquals": {
        "AWS:SourceArn": "arn:aws:cloudfront::your-account-id:distribution/PASTE_DISTRIBUTION_ID"
      }
    }
  }]
}
EOF

aws s3api put-bucket-policy \
  --bucket your-bucket-name \
  --policy file:///tmp/bucket-policy.json
```

If your Addressables build uses `UnityWebRequest` from a domain other than the CDN host, add a CORS configuration:

```bash
cat > /tmp/cors.json <<'EOF'
{
  "CORSRules": [{
    "AllowedOrigins": ["*"],
    "AllowedMethods": ["GET","HEAD"],
    "AllowedHeaders": ["*"],
    "MaxAgeSeconds": 3000
  }]
}
EOF

aws s3api put-bucket-cors \
  --bucket your-bucket-name \
  --cors-configuration file:///tmp/cors.json
```

## 3. Create the IAM user

```bash
aws iam create-user --user-name content-repo-publisher
aws iam create-access-key --user-name content-repo-publisher
```

Save the returned `AccessKeyId` and `SecretAccessKey` — you'll enter them in step 5.

Attach a minimal inline policy (list, get, put, delete on the bucket + CloudFront invalidation):

```bash
cat > /tmp/publisher-policy.json <<'EOF'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket"],
      "Resource": "arn:aws:s3:::your-bucket-name"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject","s3:PutObject","s3:DeleteObject"],
      "Resource": "arn:aws:s3:::your-bucket-name/*"
    },
    {
      "Effect": "Allow",
      "Action": ["cloudfront:CreateInvalidation","cloudfront:GetInvalidation"],
      "Resource": "arn:aws:cloudfront::your-account-id:distribution/PASTE_DISTRIBUTION_ID"
    }
  ]
}
EOF

aws iam put-user-policy \
  --user-name content-repo-publisher \
  --policy-name content-repo-publisher \
  --policy-document file:///tmp/publisher-policy.json
```

## 4. Install AWS CLI

- **macOS:** `brew install awscli`
- **Windows:** download and run the installer at `https://awscli.amazonaws.com/AWSCLIV2.msi`
- **Linux (Debian/Ubuntu):** `sudo apt-get install awscli` *(or use the `awscli-v2` zip distribution for the latest version)*

Verify:

```bash
aws --version
```

## 5. Configure credentials locally

```bash
aws configure
```

Enter:

- `AWS Access Key ID`: from step 3
- `AWS Secret Access Key`: from step 3
- `Default region name`: e.g. `eu-central-1`
- `Default output format`: `json`

## 6. Validate

1. Open **Project Settings > Content Repo > Upload** in Unity.
2. Fill in **S3 bucket name**, **S3 region**, **CloudFront distribution ID**, **CloudFront domain**.
3. Click **Validate credentials**.
   - On success: `✓ Credentials valid and bucket reachable.`
   - On failure: read the error in the inline message and console — common causes are missing AWS CLI on PATH, wrong region, or a typo in the bucket name.

## Troubleshooting

- **`aws` not on PATH** → restart Unity (and your terminal) after installing the CLI. On Windows, log out and back in if the new PATH isn't visible to the editor process.
- **`AccessDenied` on `s3 ls`** → the IAM policy is missing `s3:ListBucket` on the bucket ARN (without `/*`).
- **CloudFront still serves stale content** → the manifest path is invalidated automatically (`/<env>/manifest.json`); content paths are also invalidated, but if you bypassed the editor flow you may need to run `aws cloudfront create-invalidation` manually.
