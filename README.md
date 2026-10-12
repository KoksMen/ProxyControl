<p align="center">
  <img src="ProxyControl/unnamed.png" width="128" height="128" alt="ProxyControl Logo" />
</p>

<h1 align="center">✨ ProxyControl ✨</h1>
<h3 align="center">⚡ High-Performance Proxy & Traffic Routing Engine for Windows 🚀</h3>

<p align="center">
  <b>Высокопроизводительное, надежное и интуитивное управление прокси-серверами, системным трафиком и правилами маршрутизации в Windows</b><br>
  <i>High-performance, robust, and intuitive proxy connection and traffic routing management for Windows</i>
</p>

<p align="center">
  <a href="https://github.com/KoksMen/ProxyControl/releases"><img src="https://img.shields.io/badge/Version-v3.2.0-6366f1?style=for-the-badge&logo=github&logoColor=white" alt="ProxyControl v3.2.0" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/Architecture-MVVM%20%7C%20WPF-00B4D8?style=for-the-badge" alt="MVVM | WPF" />
  <img src="https://img.shields.io/badge/License-MIT-orange?style=for-the-badge" alt="License MIT" />
</p>

<p align="center">
  <a href="#-proxycontrol-на-русском"><b>🇷🇺 Перейти к русской версии</b></a> •
  <a href="#-proxycontrol-in-english"><b>🇬🇧 Switch to English Version</b></a>
</p>

<p align="center">
  <a href="https://github.com/KoksMen/ProxyControl/releases/latest">
    <img src="https://img.shields.io/badge/Download-Latest%20Release-success?style=for-the-badge&logo=windows" alt="Download ProxyControl" />
  </a>
</p>

---

# 🇷🇺 ProxyControl (На русском)

## 💡 О проекте

**ProxyControl** — это современный, мощный диспетчер прокси-соединений и интеллектуальной маршрутизации сетевого трафика для Windows. Приложение объединяет функции локального высокоскоростного прокси-сервера (SOCKS5 / HTTP / HTTPS), виртуального сетевого адаптера TUN на базе sing-box v1.11.4, системного контроллера WinINet и встроенного DNS-сервера с кэшированием в оперативной памяти в едином гармоничном графическом интерфейсе Fluent Dark.

Больше не требуется вручную настраивать свойства подключения Windows, страдать от 2–5-секундных зависаний браузеров при попытках подключения к IPv6 или терять соединение из-за «мертвых» прокси. ProxyControl автоматически сопоставляет сетевые потоки по гибким правилам за доли миллисекунды, мгновенно разрешает доменные имена и обеспечивает максимальную пропускную способность интернет-канала! 🚀

> [!TIP]
> Приложение поддерживает работу как в избирательном режиме (проксирование только выбранных процессов, приложений или доменов), так и в глобальном режиме прозрачного перехвата всего сетевого стека через виртуальный TUN-интерфейс.

---

## 🌟 Ключевые возможности и архитектура

### ⚡ 1. Сверхбыстрый движок сопоставления правил (FastRuleIndex)
- **Алгоритмическая сложность $O(1)$ вместо $O(N)$:** Линейный перебор сотен правил на каждый пакет заменен на высокопроизводительный **Reverse Domain Character Trie** (инвертированное префиксное дерево символов) и специализированные хэш-таблицы для IP-адресов.
- **Строгий приоритет правил:** Сопоставление гарантированно отдает предпочтение правилам с наивысшим приоритетом (`Order`), мгновенно отсекая низкоприоритетные совпадения.
- **Асинхронное определение процессов:** Неблокирующее сопоставление сетевых сокетов с процессами Windows (`GetPidByPortAsync`) без исчерпания системного пула потоков.

### 🧠 2. Локальный DNS Response TTL Cache и пул сессий SOCKS5 UDP
- **Локальный кэш ответов в памяти:** Потокобезопасный `ConcurrentDictionary<string, DnsCacheEntry>` с парсингом реального времени жизни записей (TTL) из ответов DNS и защитным диапазоном (от 10 до 3600 секунд). Повторные запросы выполняются за **0 мс**.
- **Нулевая задержка применения правил:** При любом редактировании или добавлении правил в GUI кэш моментально очищается, гарантируя мгновенное вступление изменений в силу.
- **Пул сессий SOCKS5 UDP Associate:** Повторные DNS-запросы через удаленный прокси используют существующие сессии, экономя 150–400 мс задержки на устранении повторных TCP-рукопожатий.

### 🌐 3. Режим IPv4-Only (Fast AAAA Drop) для DNS
- **Устранение задержек Happy Eyeballs:** Предотвращает 2–5-секундные зависания сайтов в браузерах при попытке подключения по IPv6 через прокси, поддерживающие только IPv4.
- **Соответствие RFC 1035:** Локальный DNS-прокси мгновенно отдает пустой ответ `NOERROR` с кодом ответа без секции Answer на любые AAAA-запросы, направляя трафик напрямую через быстрый IPv4.

### 🏎️ 4. Буферизованный сетевой конвейер и тюнинг сокетов
- **Zero-Allocation HTTP CONNECT:** Замена побайтового чтения заголовков на векторный поиск разделителя `\r\n\r\n` в пуле буферов `ArrayPool<byte>.Shared` (4 КБ) с сохранением остаточных данных (leftover), исключающая повреждение первых пакетов протоколов SSH и HTTPS.
- **Буферы сокетов 128 КБ:** Размеры буферов `ReceiveBufferSize` и `SendBufferSize` увеличены до 131 072 байт, раскрывая полную пропускную способность современных гигабитных сетей.
- **Оптимизация TCP Keep-Alive и SO_REUSEADDR:** Тонкая настройка интервалов проверки сокетов (15 секунд простоя, 3 секунды интервал повторов), защищающая от зависших разорванных соединений.

### 🛡️ 5. Умная обработка шлюзовых ошибок и безопасный сброс
- **Информативные ответы HTTP 502 / 504:** При сбое удаленного прокси клиенту отдается `502 Bad Gateway`, а при тайм-ауте — `504 Gateway Timeout` вместо непонятного разрыва соединения.
- **Быстрый повтор подключения (Fast Retry 250 мс):** Однократный автоматический повтор попытки подключения к прокси при временных сетевых сбоях до отправки полезной нагрузки.
- **Плавный SafeClose:** Завершение сокетов методом `Shutdown(Both)` с последующим `Close()`, полностью исключающее появление ошибок `ERR_CONNECTION_RESET` в веб-браузерах.

### 🎨 6. Современный интерфейс Fluent Dark (UI Modern Refresh)
- **Дизайн в стиле Windows 11:** Гармоничная темная палитра (`SurfaceDark #0C0D11`, `SurfaceMedium #14151B`, `Border #2B2E3C`).
- **Минималистичные скроллбары 10px:** Компактные закругленные полосы прокрутки с прозрачным треком и плавной индикацией при наведении.
- **Эргономичные контролы:** Переключатели ToggleSwitch с нативными пропорциями Windows 11, сегментированная навигация со скругленными пилюлями и обновленные карточки прокси с акцентными бейджами.

### 🔄 7. Многопротокольная маршрутизация и TUN-интерфейс
- **Ядро sing-box v1.11.4:** Поддержка создания виртуального сетевого интерфейса (TUN) для заворачивания всего системного трафика с проверкой контрольных сумм SHA-256.
- **Управление системным прокси Windows:** Автоматическое включение и безопасный откат настроек WinINet при запуске и завершении программы.
- **Живой мониторинг трафика:** База данных SQLite для истории подключений и мониторинг скорости в реальном времени.

---

## 🛠 Технологический стек

| Компонент | Технология | Назначение |
| :--- | :--- | :--- |
| **Платформа** | .NET 8.0 (Windows x64) | Высокопроизводительный современный рантайм |
| **Графический стек** | WPF / XAML | Fluent Dark UI, аппаратное ускорение Direct3D |
| **Архитектура** | MVVM | Чистое разделение логики и представления |
| **Сетевое ядро** | Собственный сокетный стек + sing-box v1.11.4 | SOCKS5, HTTP/HTTPS, DNS, TUN адаптер |
| **База данных** | Microsoft.Data.Sqlite 8.0 | Быстрое локальное хранение истории соединений |
| **DNS-резолвер** | DnsClient 1.8.0 + локальный TTL-кэш | Быстрое и надежное разрешение доменов |

---

## 🚀 Сборка и запуск

### Системные требования
- Windows 10 (версия 1809+) или Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (для сборки из исходного кода)

### Сборка из командной строки
```powershell
# Клонирование репозитория
git clone https://github.com/KoksMen/ProxyControl.git
cd ProxyControl

# Сборка Release-версии
dotnet build ProxyControl\ProxyControl.csproj -c Release

# Сборка единого исполняемого файла (Single-File)
dotnet publish ProxyControl\ProxyControl.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ProxyControl\bin\Release\net8.0-windows\singlefile
```

> [!NOTE]
> Для работы виртуального сетевого адаптера TUN и изменения системных параметров WinINet приложению требуются права Администратора.

---

## ✍️ Безопасность и цифровая подпись

Официальные сборки ProxyControl подписываются доверенным цифровым сертификатом Authenticode:
- **Субъект (Signer):** `CN=xXKoksMenXx, O=xXTeam, C=RU`
- **Алгоритм хэширования:** SHA-256
- **Метка времени (TSA):** RFC 3161 Timestamp (DigiCert / Microsoft)
- **Отпечаток сертификата (Thumbprint):** `C6C4A602CD20A275BC0B4E07AC0F73CAC7D414F7`

---

# 🇬🇧 ProxyControl (In English)

## 💡 About The Project

**ProxyControl** is a modern, high-performance proxy connection and intelligent traffic routing manager for Windows. The application combines the power of a local proxy server (SOCKS5 / HTTP / HTTPS), a TUN virtual network adapter powered by sing-box v1.11.4, a Windows WinINet system proxy controller, and an in-memory caching DNS server into a unified, elegant Fluent Dark user interface.

No more tedious manual network proxy switching, browser freezes caused by unreachable IPv6 endpoints, or lost connections from stalled proxies. ProxyControl matches network packets against granular routing rules in sub-millisecond time, resolves domain names instantly, and unleashes the full speed of your network connection! 🚀

> [!TIP]
> The application supports both selective routing (proxying specific applications, processes, or domain rules) and full system-wide transparent routing via the sing-box TUN network adapter.

---

## 🌟 Key Features & Architecture

### ⚡ 1. Ultra-Fast Rule Engine (FastRuleIndex)
- **Algorithmic Complexity $O(1)$ vs $O(N)$:** Replaced linear multi-pass rule iterations with a **Reverse Domain Character Trie** and hash sets for IP addresses.
- **Strict Rule Priority:** Matches always favor the rule with the highest priority (`Order`), discarding inferior matches instantaneously.
- **Async Process Resolution:** Non-blocking connection-to-process matching (`GetPidByPortAsync`) eliminating thread pool starvation.

### 🧠 2. In-Memory DNS Response TTL Cache & SOCKS5 UDP Pool
- **Concurrent In-Memory Cache:** Thread-safe `ConcurrentDictionary<string, DnsCacheEntry>` with dynamic TTL clamping (10s to 3600s). Cached queries resolve in **0 ms**.
- **Instant Rule Propagation:** Any rule modification or addition immediately purges the DNS cache, guaranteeing zero latency when updating configurations.
- **SOCKS5 UDP Session Pool:** Reuses established UDP associate tunnels, saving 150–400 ms on each DNS lookup by eliminating redundant TCP handshakes.

### 🌐 3. IPv4-Only Mode (Fast AAAA Drop)
- **Eliminates Happy Eyeballs Delays:** Avoids 2–5 second connection stalls in browsers attempting IPv6 connections through IPv4-only upstream proxies.
- **RFC 1035 Compliant:** Instantly returns empty `NOERROR` DNS responses with zero answers for AAAA queries, directing browsers straight to IPv4.

### 🏎️ 4. Buffered Network Pipeline & Socket Tuning
- **Zero-Allocation HTTP CONNECT:** Handshake parser uses vector search for `\r\n\r\n` over `ArrayPool<byte>.Shared` (4 KB) with leftover preservation, preventing handshake corruption on protocols like SSH and HTTPS.
- **128 KB Socket Buffers:** Expanded `ReceiveBufferSize` and `SendBufferSize` to 131,072 bytes for maximum throughput on gigabit connections.
- **Keep-Alive & SO_REUSEADDR:** Hardened socket keep-alive probes (15s idle, 3s retry interval) preventing leaked connections.

### 🛡️ 5. Resilient Gateway Error Handling & SafeClose
- **Descriptive HTTP 502 / 504 Responses:** Upstream failures return standard `502 Bad Gateway` and timeouts yield `504 Gateway Timeout` instead of silent connection resets.
- **Fast Retry (250 ms):** Single fast connection retry on upstream proxy SYN drops before transmitting user payload.
- **Graceful SafeClose:** Sockets close cleanly via `Shutdown(Both)` followed by `Close()`, eliminating `ERR_CONNECTION_RESET` in client browsers.

### 🎨 6. Modern Fluent Dark UI Refresh
- **Windows 11 Fluent Design:** Clean dark palette (`SurfaceDark #0C0D11`, `SurfaceMedium #14151B`, `Border #2B2E3C`).
- **Minimal 10px Scrollbars:** Floating rounded thumbs with transparent tracks and smooth hover transitions.
- **Ergonomic Controls:** Native Windows 11-style toggle switches, segmented pill navigation, and card styling with accent badges.

### 🔄 7. Multi-Protocol Routing & TUN Adapter
- **sing-box v1.11.4 Engine:** Virtual TUN network adapter support for full system-wide proxying with SHA-256 binary verification.
- **WinINet Management:** Automatic configuration and graceful restoration of Windows system proxy settings.
- **Live Traffic Monitoring:** SQLite-backed connection history and real-time bandwidth meters.

---

## 🛠 Tech Stack

| Component | Technology | Purpose |
| :--- | :--- | :--- |
| **Runtime** | .NET 8.0 (Windows x64) | High-performance modern managed runtime |
| **UI Framework** | WPF / XAML | Fluent Dark UI, Direct3D hardware acceleration |
| **Design Pattern** | MVVM | Clean separation of UI and business logic |
| **Networking** | Custom Sockets + sing-box v1.11.4 | SOCKS5, HTTP/HTTPS, DNS, TUN interface |
| **Database** | Microsoft.Data.Sqlite 8.0 | Embedded connection history store |
| **DNS Client** | DnsClient 1.8.0 + TTL Cache | Fast, resilient DNS lookup pipeline |

---

## 🚀 Building & Running

### Requirements
- Windows 10 (1809+) or Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build Commands
```powershell
# Clone the repository
git clone https://github.com/KoksMen/ProxyControl.git
cd ProxyControl

# Build Release
dotnet build ProxyControl\ProxyControl.csproj -c Release

# Publish Single-File Executable
dotnet publish ProxyControl\ProxyControl.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ProxyControl\bin\Release\net8.0-windows\singlefile
```

---

## ✍️ Code Signing & Security

Official ProxyControl releases are cryptographically signed with Authenticode:
- **Signer:** `CN=xXKoksMenXx, O=xXTeam, C=RU`
- **Hash Algorithm:** SHA-256
- **Timestamp Authority (TSA):** RFC 3161 Timestamp (DigiCert / Microsoft)
- **Thumbprint:** `C6C4A602CD20A275BC0B4E07AC0F73CAC7D414F7`

---

## 📄 License

This project is licensed under the **MIT License**. See [LICENSE](LICENSE) for details.
