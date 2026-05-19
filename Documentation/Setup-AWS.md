# Setup — AWS S3 + CloudFront

One-time setup for the Content Repo upload pipeline using the bundled `AwsUploadProvider`. Replace `your-bucket-name`, `your-account-id`, and `eu-central-1` with your values throughout.

---

## 1. Create an AWS account

1. Go to **https://aws.amazon.com** and click **Create an AWS Account**.
2. Enter your email address and choose an account name (e.g. `my-studio`).
3. Choose **Personal** or **Business** — either works.
4. Enter payment details. AWS won't charge you unless you exceed the free tier.
5. Complete phone verification and select the **Free** support plan.

You are now signed in as the **root user**. The root user has unrestricted access to everything and should not be used for day-to-day work.

## 2. Secure the root account

1. In the top-right corner, open the account menu and go to **Security credentials**.
2. Under **Multi-factor authentication (MFA)**, click **Assign MFA device** and follow the prompts to register an authenticator app.
3. After saving MFA, sign out. From this point forward, use an IAM admin user (created in step 3) for all work — reserve root login for account-level emergencies only.

## 3. Create an IAM admin user

This user runs all the setup CLI commands in steps 5–7. After setup you can delete it or keep it for future infrastructure changes.

1. In the AWS console, search for **IAM** and open it.
2. Click **Users → Create user**.
3. Name it `admin` (or anything you like), then click **Next**.
4. Choose **Attach policies directly**, search for `AdministratorAccess`, tick it, then click **Next → Create user**.
5. Open the user, go to the **Security credentials** tab, and click **Create access key**.
6. Select **Command Line Interface (CLI)**, acknowledge the warning, and click **Next → Create access key**.
7. **Copy and save the Access Key ID and Secret Access Key** — the secret is shown only once.

## 4. Install the AWS CLI

- **macOS:** `brew install awscli`
- **Windows:** download and run `https://awscli.amazonaws.com/AWSCLIV2.msi`, then open a new terminal.
- **Linux (Debian/Ubuntu):** `sudo apt-get install awscli`

Verify:

```bash
aws --version
# aws-cli/2.x.x ...
```

## 5. Configure the admin credentials

```bash
aws configure
```

Enter:

- **AWS Access Key ID** — from step 3
- **AWS Secret Access Key** — from step 3
- **Default region name** — e.g. `eu-central-1`
- **Default output format** — `json`

Test that everything works:

```bash
aws sts get-caller-identity
```

You should see your account ID and the `admin` user ARN.

---

## 6. Create the S3 bucket

```bash
aws s3api create-bucket \
  --bucket your-bucket-name \
  --region eu-central-1 \
  --create-bucket-configuration LocationConstraint=eu-central-1
```

Block all public access (CloudFront reads the bucket via Origin Access Control, not the public web):

```bash
aws s3api put-public-access-block \
  --bucket your-bucket-name \
  --public-access-block-configuration \
  "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"
```

## 7. Create the CloudFront distribution

Create an Origin Access Control (OAC) in S3-signing mode:

```bash
aws cloudfront create-origin-access-control \
  --origin-access-control-config \
  Name=content-repo-oac,SigningProtocol=sigv4,SigningBehavior=always,OriginAccessControlOriginType=s3
```

Note the returned `Id` — you will paste it into the distribution config below.

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

Record the returned `DistributionId` and `DomainName` (e.g. `d111111abcdef8.cloudfront.net`) — both go into Project Settings later.

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

## 8. Create the IAM publisher user

This is the minimal-permission user whose credentials you store in Unity. It can only read/write/delete inside your bucket and trigger CloudFront invalidations — nothing else.

```bash
aws iam create-user --user-name content-repo-publisher
aws iam create-access-key --user-name content-repo-publisher
```

**Save the returned `AccessKeyId` and `SecretAccessKey`** — you'll enter them in step 9.

Attach the inline policy:

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

## 9. Switch the CLI to the publisher credentials

Unity's **Validate credentials** button calls the CLI as the current default profile, so point it at the publisher user:

```bash
aws configure
```

Enter the `content-repo-publisher` Access Key ID and Secret Access Key from step 8. Leave region and output format unchanged.

Alternatively, use a named profile and set `AWS_PROFILE=content-repo-publisher` in your environment so the admin credentials remain the default.

## 10. Configure and validate in Unity

1. Open **Project Settings > Content Repo > Upload**.
2. Fill in:
   - **S3 bucket name** — `your-bucket-name`
   - **S3 region** — e.g. `eu-central-1`
   - **CloudFront distribution ID** — from step 7
   - **CloudFront domain** — e.g. `d111111abcdef8.cloudfront.net`
3. Click **Validate credentials**.
   - On success: `✓ Credentials valid and bucket reachable.`
   - On failure: read the error in the inline message and console — common causes are missing AWS CLI on PATH, wrong region, or a typo in the bucket name.

---

## Troubleshooting

- **`aws` not on PATH** — restart Unity (and your terminal) after installing the CLI. On Windows, log out and back in if the new PATH isn't visible to the editor process.
- **`AccessDenied` on `s3 ls`** — the IAM policy is missing `s3:ListBucket` on the bucket ARN (without `/*`).
- **`InvalidClientTokenId`** — the access key was entered incorrectly or belongs to a deleted user. Run `aws sts get-caller-identity` to confirm which identity the CLI is currently using.
- **CloudFront still serves stale content** — the manifest path is invalidated automatically (`/<env>/manifest.json`); if you bypassed the editor flow you may need to run `aws cloudfront create-invalidation` manually.
