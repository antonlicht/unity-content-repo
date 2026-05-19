using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    internal sealed class AwsLoginWindow : EditorWindow
    {
        public static void Open()
        {
            var win = GetWindow<AwsLoginWindow>(true, "Configure AWS Credentials", true);
            win.minSize = win.maxSize = new Vector2(420, 200);
        }

        private TextField _keyIdField;
        private TextField _secretField;
        private TextField _regionField;
        private Button _saveBtn;
        private Label _statusLabel;

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft   = 14;
            root.style.paddingRight  = 14;
            root.style.paddingTop    = 14;
            root.style.paddingBottom = 14;

            root.Add(new Label("Enter the Access Key ID and Secret Access Key for your publisher IAM user.")
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 10 }
            });

            _keyIdField  = new TextField("Access Key ID");
            _secretField = new TextField("Secret Access Key") { isPasswordField = true };
            _regionField = new TextField("Region") { value = ContentUploadSettings.instance.S3Region };

            root.Add(_keyIdField);
            root.Add(_secretField);
            root.Add(_regionField);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 14 } };
            _saveBtn = new Button(OnSave) { text = "Save", style = { marginRight = 6 } };
            row.Add(_saveBtn);
            row.Add(new Button(Close) { text = "Cancel" });
            root.Add(row);

            _statusLabel = new Label { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
            root.Add(_statusLabel);
        }

        private async void OnSave()
        {
            var keyId  = _keyIdField.value.Trim();
            var secret = _secretField.value.Trim();
            var region = _regionField.value.Trim();

            if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(region))
            {
                _statusLabel.text = "All fields are required.";
                return;
            }

            _saveBtn.SetEnabled(false);
            _statusLabel.text = "Saving…";
            try
            {
                await ContentInfraApi.ConfigureCredentialsAsync(keyId, secret, region);
                _statusLabel.text = "✓ Saved.";
                await System.Threading.Tasks.Task.Delay(800);
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.text = $"✗ {ex.Message}";
                _saveBtn.SetEnabled(true);
            }
        }
    }
}
