using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    public enum UploadProviderType
    {
        AWS_S3_CloudFront,
    }

    [FilePath("ProjectSettings/ContentRepoUpload.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentUploadSettings : ScriptableSingleton<ContentUploadSettings>
    {
        [SerializeField] private UploadProviderType providerType = UploadProviderType.AWS_S3_CloudFront;
        [SerializeField] private string s3BucketName = "";
        [SerializeField] private string s3Region = "eu-central-1";
        [SerializeField] private string cloudFrontDistributionId = "";
        [SerializeField] private string cloudFrontDomain = "";
        [SerializeField] private string stagingPrefix = "staging";
        [SerializeField] private string productionPrefix = "production";

        public UploadProviderType ProviderType => providerType;
        public string S3BucketName => s3BucketName;
        public string S3Region => s3Region;
        public string CloudFrontDistributionId => cloudFrontDistributionId;
        public string CloudFrontDomain => cloudFrontDomain;
        public string StagingPrefix => stagingPrefix;
        public string ProductionPrefix => productionPrefix;

        public string GetEnvironmentPrefix(BuildEnvironment env) => env switch
        {
            BuildEnvironment.Staging => stagingPrefix,
            BuildEnvironment.Production => productionPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(env), env, null),
        };

        public void Persist() => Save(true);
    }

    internal static class ContentUploadSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Content Repo/Upload", SettingsScope.Project)
            {
                label = "Upload",
                keywords = new HashSet<string>(new[] { "content", "upload", "aws", "s3", "cloudfront", "cdn" }),
                activateHandler = (_, root) =>
                {
                    var settings = ContentUploadSettings.instance;
                    settings.hideFlags &= ~HideFlags.NotEditable;

                    var so = new SerializedObject(settings);

                    var container = new VisualElement
                    {
                        style =
                        {
                            paddingLeft = 10, paddingRight = 10, paddingTop = 10, paddingBottom = 10,
                        }
                    };

                    container.Add(new Label("Content Repo — Upload")
                    {
                        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8 }
                    });

                    container.Add(new PropertyField(so.FindProperty("providerType"), "Upload provider"));
                    container.Add(new PropertyField(so.FindProperty("s3BucketName"), "S3 bucket name"));
                    container.Add(new PropertyField(so.FindProperty("s3Region"), "S3 region"));
                    container.Add(new PropertyField(so.FindProperty("cloudFrontDistributionId"), "CloudFront distribution ID"));
                    container.Add(new PropertyField(so.FindProperty("cloudFrontDomain"), "CloudFront domain"));
                    container.Add(new PropertyField(so.FindProperty("stagingPrefix"), "Staging environment prefix"));
                    container.Add(new PropertyField(so.FindProperty("productionPrefix"), "Production environment prefix"));

                    var loginBtn = new Button(AwsLoginWindow.Open)
                    {
                        text = "Configure credentials…",
                        style = { marginTop = 10, alignSelf = Align.FlexStart }
                    };
                    container.Add(loginBtn);

                    var validateBtn = new Button { text = "Validate credentials", style = { marginTop = 4, alignSelf = Align.FlexStart } };
                    var resultLabel = new Label("") { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
                    container.Add(validateBtn);
                    container.Add(resultLabel);

                    validateBtn.clicked += async () =>
                    {
                        validateBtn.SetEnabled(false);
                        resultLabel.text = "Validating…";
                        try
                        {
                            var provider = ContentUploadProviderFactory.Resolve();
                            var ok = await provider.ValidateConfigAsync();
                            resultLabel.text = ok
                                ? "✓ Credentials valid and bucket reachable."
                                : "✗ Validation failed — check console and Setup-AWS.md.";
                        }
                        catch (Exception ex)
                        {
                            resultLabel.text = $"✗ {ex.Message}";
                        }
                        finally
                        {
                            validateBtn.SetEnabled(true);
                        }
                    };

                    container.Bind(so);
                    container.TrackSerializedObjectValue(so, _ => settings.Persist());

                    root.Add(container);
                }
            };
        }
    }
}
