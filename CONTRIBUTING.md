# المساهمة · Contributing

[العربية](#عربي) · [English](#english)

<a id="عربي"></a>
<div dir="rtl">

## حيّاك 👋

شكراً إنك حاب تساعد في **Fastboot Studio**. أي مساعدة مرحّب فيها، ما تحتاج تكون مبرمج.

### بلاغ عن مشكلة
افتح [بلاغ جديد](https://github.com/ifoknr/FastbootStudio/issues/new/choose) واختر «مشكلة»، واكتب:
- موديل الجوال والمعالج (ميدياتك، كوالكوم، Unisoc…).
- الوضع: bootloader أو fastbootd أو أندرويد.
- وش سويت بالضبط، ووش صار، ووش كنت تتوقع.
- نسخة من صفحة **السجل** داخل البرنامج (زر «نسخ»).

### فكرة أو اقتراح
افتح بلاغ واختر «فكرة»، واشرح وش المشكلة اللي تحلها الفكرة.

### الترجمة
النصوص في `Properties/Resources.resx` (الإنجليزي) و `Properties/Resources.ar.resx` (العربي).
عدّل النص بين `<value>` و `</value>` فقط، وخل اسم المفتاح زي ما هو.

### الكود
1. سوِّ Fork للمستودع، وافتح فرع جديد من `master`.
2. ابنِ وشغّل الاختبارات (الأوامر تحت).
3. افتح Pull Request واشرح وش غيّرت وليش.

كل PR يمر على بناء ويندوز، واختبارات المكتبة، وتشغيل كامل للبرنامج على جوال وهمي يصوّر كل الصفحات.

</div>

---

<a id="english"></a>
## Welcome 👋

Thanks for wanting to help with **Fastboot Studio**. Every kind of help is welcome; you do
not need to be a programmer.

### Reporting a bug
Open a [new issue](https://github.com/ifoknr/FastbootStudio/issues/new/choose), pick **Bug report**, and include:
- the phone model and chipset (MediaTek, Qualcomm, Unisoc…);
- the mode: bootloader, fastbootd or Android;
- what you did, what happened and what you expected;
- a copy of the app's **Logs** page (the Copy button).

### Ideas
Open an issue and pick **Idea**; describe the problem the idea solves.

### Translations
Strings live in `Properties/Resources.resx` (English) and `Properties/Resources.ar.resx` (Arabic).
Change only the text between `<value>` and `</value>` and keep the key names. A new language
is a new `Resources.<code>.resx` file with the same keys.

### Code
1. Fork the repository and branch from `master`.
2. Build and run the tests:
   ```
   dotnet build FastbootEnhance.csproj -c Release
   dotnet test tests/FastbootEnhance.Core.Tests/FastbootEnhance.Core.Tests.csproj
   ```
   The app needs Windows (WPF); the core library and its tests also run on Linux and macOS.
3. Open a pull request that says what changed and why.

Logic that does not need the UI (payload, sparse, super, fastboot variables, safety checks)
belongs in `src/FastbootEnhance.Core` with a test in `tests/`. Every pull request is built on
Windows, runs the core tests, and drives the whole app against a fake phone
(`.github/scripts/screenshots.ps1`), so a new page or dialog can be checked there too.

Questions: Telegram [@IFOKNR1](https://t.me/IFOKNR1).
