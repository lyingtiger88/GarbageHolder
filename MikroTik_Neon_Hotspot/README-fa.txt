# MikroTik Neon Hotspot

فایل‌ها:
- login.html : صفحه ورود
- status.html : داشبورد بعد از ورود
- status-data.html : endpoint سبک برای دریافت آمار زنده
- assets/style.css : ظاهر
- assets/status.js : محاسبه سرعت و رسم نمودار ثانیه‌ای

## نصب
همه فایل‌ها را داخل پوشه Hotspot میکروتیک کپی کنید و ساختار پوشه assets را حفظ کنید.

## نمودار زنده
status.js هر 1 ثانیه status-data.html را با cache-buster می‌خواند.
سرعت دانلود و آپلود از اختلاف bytes-in / bytes-out بین دو نمونه محاسبه می‌شود.

در MikroTik Hotspot:
- bytes-in = آپلود کلاینت به روتر
- bytes-out = دانلود روتر به کلاینت

## CHAP
login.html از md5.js استاندارد Hotspot استفاده می‌کند. فایل md5.js معمولاً از قبل در پوشه hotspot وجود دارد.
