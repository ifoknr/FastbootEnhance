# Changelog · سجل التعديلات

[العربية](#عربي) · [English](#english)

<!-- The "## vX.Y.Z" section is posted to Telegram with the release (.github/scripts/telegram-release.sh): keep it short. -->

## v2.0.0
- واجهة داكنة جديدة باللغة العربية وأيقونة جديدة
- تفليش التحديثات مع التحقق من كل صورة قبل الكتابة
- أدوات الصور: simg2img و lpunpack وجمع أجزاء كوالكوم
- بناء Super مطابق لأداة أندرويد الرسمية
- نسخ احتياطي للأقسام والملفات
- لا يحتاج تثبيت: adb و fastboot مدمجة

## تفاصيل الإصدار 2.0
<a id="عربي"></a>
<div dir="rtl">

كل ما تغيّر مقارنة ببرنامج Fastboot Enhance الأصلي (1.x) للمطوّر LibXZR.

### 🎨 الواجهة والتصميم
- **تصميم داكن جديد بالكامل:**
  - قائمة جانبية بأيقونات، وبطاقات، وألوان هادئة.
  - كل النوافذ والرسائل بنفس التصميم، بما فيها شريط العنوان ونوافذ التأكيد.
- **اللغة العربية:**
  - واجهة كاملة من اليمين لليسار.
  - خط **Noto Kufi Arabic** للعربي و **Roboto** للإنجليزي، مدمجان داخل البرنامج.
  - تبديل اللغة من صفحة «حول».
- **العمليات الخطرة** (مسح، حذف، تفليش بدون تحقق) تطلب تأكيداً واضحاً باللون الأحمر.
- **أيقونة جديدة هادئة.**
- **الاسم الجديد:** Fastboot Studio، مع صفحة «حول» فيها الحقوق والرخصة وروابط المطوّر.

### 📱 الجهاز والتفليش
- **صفحة تفليش مستقلة لملفات التحديث:**
  - استخراج كل الصور بالتوازي.
  - التحقق من SHA-256 لكل صورة **قبل** كتابة أي شيء على الجوال.
  - تقدّم كل قسم وسجل مباشر.
- **فحوصات ما قبل التفليش:** البوت لودر مفتوح أو مقفل، وضع fastbootd، تحديث معلّق، أقسام COW، الخانة الحالية.
- **إلغاء تحديث Virtual A/B المعلّق** من داخل البرنامج.
- **منع الإغلاق أثناء التفليش** إلا بتأكيد، حتى لا يبقى الجوال نصف مفلّش.
- **مهلات زمنية ذكية لأوامر fastboot:** لا يعلّق البرنامج إذا توقف الجوال، ولا يقطع كتابة صورة كبيرة على USB بطيء.
- **أدوات adb و fastboot مدمجة** (platform-tools 37.0.1)، فلا تحتاج تثبيت أي شيء.

### 📦 مستخرج Payload
- **محرك استخراج جديد بالكامل:**
  - يدعم كل أنواع ضغط تحديثات A/B الحديثة (zstd و xz و bzip2 و zero و discard).
  - يعمل بالتوازي.
- **قراءة ملف التحديث zip من مكانه** بدون فك ضغطه على القرص، ويدعم ZIP64.
- **فتح تحديثات A/B الحديثة:** البرنامج الأصلي ما كان يفتحها أصلاً، لأنها مضغوطة بـ zstd وفيها عمليات تكتب إلى أكثر من موضع (extent). الآن تُكتب كل المواضع بترتيبها كما يفعل أندرويد.
- **رسائل واضحة** للتحديثات التزايدية والعمليات غير المدعومة، لكل قسم على حدة.

### 🧱 أدوات الصور (جديد)
- **فحص أي صورة:** sparse و raw و super و ext4 و EROFS و F2FS و boot و vendor_boot و vbmeta و DTBO.
- **تحويل الصيغ:**
  - `simg2img`: من sparse إلى raw، مع الأجزاء المقسّمة وفحص CRC-32.
  - `img2simg` و `simg2simg`: من raw إلى sparse، والتقسيم إلى أجزاء.
  - النتيجة مطابقة لأدوات أندرويد بايت ببايت.
- **`lpunpack`:** فك super مباشرة من صورة sparse أو مقسّمة أو raw.
- **حزم تفليش كوالكوم:**
  - فتح الأقسام المقسّمة (`super_1.img`…) حسب ملف `rawprogram*.xml` كصورة واحدة.
  - جمعها في ملف واحد بصيغة raw أو sparse.

### 🧩 بناء Super (جديد)
- **بناء `super.img`:**
  - من صور الأقسام، بتقسيم Virtual A/B أو A/B أو خانة واحدة.
  - أو بنفس تقسيم ملف super موجود.
- **قراءة حجم super ونوع التقسيم من الجوال** مباشرة.
- **العثور التلقائي على الصور المفكوكة**، فتصير خطوات فك ← تعديل ← إعادة بناء سهلة.
- **مطابقة لأداة أندرويد الرسمية:** الناتج **مطابق بايت ببايت** لأداة liblp/lpmake، وكل بناء يُقرأ من جديد ويُتحقق من كل قسم فيه.

### 💾 النسخ الاحتياطي (جديد)
- **نسخ الأقسام:**
  - عبر adb، بالروت (Magisk أو KernelSU) أو من ريكفري مخصص.
  - «تحديد المهم» يختار الأقسام الحساسة مثل IMEI و persist و modem.
  - بصمة **SHA-256** لكل صورة، وملف معلومات للجوال، وزر لفحص النسخة لاحقاً.
- **نسخ أي ملف أو مجلد** من الجوال إلى الكمبيوتر بدون روت.
- **دعم الجوال في وضع الفاست بوت:**
  - شرح ليش ما ينفع النسخ من هذا الوضع.
  - أزرار لإعادة التشغيل إلى أندرويد أو الريكفري.
  - تشغيل صورة TWRP أو OrangeFox مؤقتاً بدون تفليشها.

### 🐞 أخطاء البرنامج الأصلي التي أُصلحت
- **نجاح وهمي للتفليش:** كان التفليش يُعتبر ناجحاً مهما كانت نتيجة fastboot، والآن تُفحص النتيجة.
- **تعليق البرنامج:** كان أمر fastboot المعلّق يعلّق البرنامج، أو يبقى ممسكاً بالجوال بعد الإغلاق.
- **مسار fastboot.exe:** كان يُبحث عنه في مجلد التشغيل بدل مجلد البرنامج، فيفشل عند التشغيل من اختصار.
- **أسماء أقسام خطرة:** أسماء الأقسام من ملف التحديث كانت تُستخدم كأسماء ملفات مباشرة، فاسم مثل `../../x` يكتب خارج المجلد المختار.
- **فرق التوقيت:** التواريخ كانت تُحوّل بفرق توقيت ثابت (UTC+8).
- **التحديثات بدون تواقيع:** عرض معلومات تحديث بدون تواقيع كان يسبب خطأ.

### ⚙️ تحت الغطاء
- **نقل البرنامج إلى ‎.NET 8**، كملف تنفيذي واحد لا يحتاج تثبيت.
- **فصل المحرك عن الواجهة** في مكتبة مستقلة (FastbootEnhance.Core)، مع أداة سطر أوامر:
  `info` و `extract` و `verify` و `imginfo` و `simg2img` و `img2simg` و `lpunpack` و `combine` و `mksuper`.
- **137 اختباراً آلياً**، منها مقارنات بايت ببايت مع أدوات أندرويد الرسمية.
- **اختبار تلقائي على ويندوز** يشغّل البرنامج بجوال وهمي:
  - يمر على كل الصفحات بالعربي والإنجليزي.
  - يتحقق من نتيجة كل عملية بالبصمة.
  - يحفظ صور الشاشات.

</div>

---

<a id="english"></a>
## Version 2.0 in detail

Everything that changed compared with the original Fastboot Enhance 1.x by LibXZR.

### 🎨 Interface
- A new dark design throughout: navigation rail with icons, cards, calm colours. Every window and
  message box, title bar included, uses the same theme.
- **Arabic** with a complete right-to-left layout; **Noto Kufi Arabic** and **Roboto** embedded;
  language switch on the About page.
- Destructive actions (erase, delete, unverified flash) ask clearly, in red.
- New, calmer icon. New name, Fastboot Studio, with an About page for credits, license and links.

### 📱 Device and flashing
- A dedicated **Flash** page for OTAs: every image extracted in parallel and checked against its
  SHA-256 **before** anything is written; per-partition progress and a live log.
- **Pre-flash checks**: bootloader lock state, fastbootd, pending update, leftover COW partitions,
  current slot.
- **Cancel a pending Virtual A/B update** from the app.
- Closing the app mid-flash needs confirmation.
- Sensible fastboot time limits: a hung phone no longer freezes the app, and slow USB never cuts a
  large image off halfway.
- adb and fastboot (platform-tools 37.0.1) bundled.

### 📦 Payload Dumper
- New extraction engine: every compression a current A/B OTA uses (zstd, xz, bzip2, zero,
  discard), decoded in parallel.
- OTA zips read in place, never unpacked to disk; ZIP64 handled.
- Current A/B OTAs open: the original refused them (zstd, and operations writing to several extents,
  "Multiple dst in one operation"). Extents are now written in manifest order, as AOSP does.
- Clear, per-partition messages for incremental packages and unsupported operations.

### 🧱 Image Tools (new)
- Inspect sparse, raw, super, ext4, EROFS, F2FS, boot, vendor_boot, vbmeta and DTBO images.
- `simg2img` (split parts, CRC-32 checks), `img2simg` and `simg2simg`, byte-identical to AOSP.
- `lpunpack` straight from sparse, split or raw super.
- Qualcomm flash packages: partitions in pieces (`super_1.img` … via `rawprogram*.xml`) open as one
  image and can be combined into one raw or sparse file.

### 🧩 Build Super (new)
- Build `super.img` from partition images: Virtual A/B, A/B, single slot, or the layout of an
  existing super.
- Read the size of super and the slot layout from the phone.
- Unpacked images are found by themselves: unpack → modify → rebuild.
- Output byte-identical to AOSP liblp/lpmake; every build is read back and checked partition by
  partition.

### 💾 Backup (new)
- Partition backups over adb with root (Magisk / KernelSU) or a custom recovery; critical
  partitions (IMEI, persist, modem…) preselected; SHA-256 for every image, a device info file, and
  a check for later.
- Copy any file or folder from the phone, no root needed.
- A phone in fastboot is recognised: the page explains why, reboots it to Android or recovery, or
  boots a TWRP / OrangeFox image once without flashing it.

### 🐞 Bugs of the original that were fixed
- A flash reported success whatever fastboot returned; the exit code is checked now.
- A hung fastboot call froze the app, or kept holding the USB device after closing.
- fastboot.exe was looked up in the working directory, so starting from a shortcut could fail.
- Partition names from the manifest were used as file names as they were; `../../x` wrote outside the
  chosen folder.
- Timestamps were converted with a fixed UTC+8 offset.
- Showing a package without signatures threw.

### ⚙️ Under the hood
- Ported to .NET 8; one self-contained executable.
- Engine split into a UI-free library (FastbootEnhance.Core) with a command line tool:
  `info`, `extract`, `verify`, `imginfo`, `simg2img`, `img2simg`, `lpunpack`, `combine`, `mksuper`.
- 137 automated tests, including byte-for-byte comparisons with AOSP's own tools.
- A Windows CI run drives the real app against fake devices through every page, in English and
  Arabic, checks each result by hash and saves the screenshots.

## v1.4.0
- First FastbootStudio release, based on FastbootEnhance by xzr467706992.
- Fastboot toolbox and Payload.bin dumper for Windows: fastboot vars, reboot to
  fastbootd, bootloader, recovery or system, switch A/B slots, flash and erase
  partitions, manage logical partitions, flash Payload.bin in fastbootd and extract
  single images from it.
- English, Chinese, Japanese and Korean.
