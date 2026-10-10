# Signing the Windows build

The release workflow can send `FastbootStudio.exe` to SignPath for an Authenticode signature
before it is zipped. It is off until SignPath is set up; releases are built unsigned until then.

<div dir="rtl">

## ليش التوقيع
- يخفف إنذارات Microsoft Defender الكاذبة، اللي أوقفت طلب winget مرة.
- يقلّل تحذير SmartScreen الأزرق "Windows protected your PC" عند الناس.
- يثبت إن الملف طالع من مستودعك وما أحد عدّل عليه.

## الخطوات (مرة وحدة)
1. **قدّم على SignPath Foundation** (مجاني للمشاريع المفتوحة) من https://signpath.org (زر Apply).
   - المشروع: `https://github.com/ifoknr/FastbootStudio`، الترخيص MIT.
   - سياسة التوقيع موجودة في [`CODE_SIGNING.md`](../../CODE_SIGNING.md).
   - فعّل التحقق بخطوتين (2FA) في حساب GitHub، لأنهم يطلبونه.
2. بعد القبول، في SignPath:
   - المشروع اسمه (slug) `FastbootStudio`.
   - **Artifact configuration**: الصق محتوى [`artifact-configuration.xml`](artifact-configuration.xml). اسمها الافتراضي `initial`.
   - **Signing policy** اسمها `release-signing`، وربطها بـ GitHub كـ Trusted Build System.
   - من حسابك في SignPath خذ **Organization ID** و**API token**.
3. في GitHub افتح المستودع ← **Settings ← Secrets and variables ← Actions**:
   - تبويب **Secrets**: أضف `SIGNPATH_API_TOKEN` = الـ API token.
   - تبويب **Variables**: أضف `SIGNPATH_ORGANIZATION_ID` = الـ Organization ID.
   - لو الأسماء في SignPath غير اللي فوق، أضف أيضاً `SIGNPATH_PROJECT_SLUG` أو `SIGNPATH_POLICY_SLUG` أو `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`.

بعدها كل إصدار من Release ينتظر موافقتك على التوقيع في SignPath (يجيك إيميل)، وبعد الموافقة يكمل وينشر الملف موقّع.

</div>

## Setup (once)

1. Apply to the SignPath Foundation (free for open source) at https://signpath.org.
   The policy it asks for is [`CODE_SIGNING.md`](../../CODE_SIGNING.md).
2. In SignPath: project slug `FastbootStudio`; artifact configuration `initial` with the
   contents of [`artifact-configuration.xml`](artifact-configuration.xml); signing policy
   `release-signing` with GitHub as trusted build system.
3. In GitHub, Settings → Secrets and variables → Actions:
   - secret `SIGNPATH_API_TOKEN`
   - variable `SIGNPATH_ORGANIZATION_ID`
   - optional variables `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`,
     `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` when the names differ from the defaults above.

## What the workflow does

In the `build (windows)` job of [`release.yml`](../../.github/workflows/release.yml), after
`dotnet publish`:

1. Uploads `publish/FastbootStudio.exe` as an artifact.
2. `signpath/github-action-submit-signing-request@v3` submits it and waits for the signed file
   (an approver confirms the request in SignPath).
3. Checks the signature with `Get-AuthenticodeSignature`; the release stops if it is not valid.
4. Puts the signed exe back before the zip is made, so the zip, its SHA256 and the winget
   manifests all describe the signed build.

Only `FastbootStudio.exe` is signed. Google's adb and fastboot files are shipped as Google built them.
