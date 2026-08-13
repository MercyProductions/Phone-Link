# Phase 1: iPhone integration feature feasibility

Reviewed: **2026-08-12**

This document defines what a custom Windows application and an optional SwiftUI iPhone companion can legitimately do using current production Apple SDKs, public Windows APIs, and published Bluetooth profiles. It is a product boundary, not a list of aspirations.

## Scope and baseline

- Production baseline: iOS 26.x. Apple currently lists Xcode 26.6 on macOS Tahoe 26.2+ with the iOS 26.5 SDK. iOS 27 APIs are beta and are not used as a product dependency. See [Xcode SDK and system requirements](https://developer.apple.com/xcode/system-requirements/).
- The global baseline must work outside the European Union. EU-only iOS 26 capabilities are listed separately.
- "Public API" includes documented Apple frameworks, documented Windows SDK APIs, and published Bluetooth profiles. It does not include Microsoft Phone Link internals.
- The iPhone companion cannot use another app's sandbox, database, notification store, or credentials.

## Category definitions

1. **Fully supported** — implementable in the Windows app with stable public Windows/platform APIs and no special iPhone privilege.
2. **Possible with limitations** — a public route exists, but it is session-bound, permission-gated, region-gated, background-limited, profile-dependent, or needs a hardware proof.
3. **Requires an iPhone companion app** — legitimate and implementable only because our signed SwiftUI app collects or creates the data with user permission.
4. **Impossible using public APIs** — no supported API provides the requested access. The product must not simulate or promise it.

Categories describe the primary implementation route. A feature may have a second, more limited route noted in the details.

## Feasibility matrix

| Feature | Category | Legitimate implementation and exact boundary |
|---|---:|---|
| Windows app shell, navigation, tray operation, dark/light themes, local search, Windows toasts | **1** | Standard WinUI 3/.NET and Windows App SDK functionality. |
| SQLite data for paired devices, synchronized contacts, app-owned conversations/events, notifications, settings, and audit data | **1** | Store only data received through authorized routes. SQLite is an app store, not a way to acquire iPhone data. |
| Pair the two custom apps with a QR code | **3** | Windows creates a short-lived pairing offer containing its endpoint, nonce, protocol version, and public key. The iPhone companion scans it and completes an authenticated key exchange. |
| Encrypt and authenticate local traffic | **3** | Use TLS 1.3 plus application-level device identity. CryptoKit supports signing/key handling and Keychain storage; Windows protects local secrets with `DataProtectionProvider("LOCAL=user")`. See [CryptoKit](https://developer.apple.com/documentation/cryptokit/), [storing CryptoKit keys in Keychain](https://developer.apple.com/documentation/cryptokit/storing-cryptokit-keys-in-the-keychain), and [Windows data protection](https://learn.microsoft.com/windows/uwp/security/data-protection). |
| Discover the companion over local Wi-Fi | **3** | Bonjour/mDNS discovery plus a standard TLS connection is supported. iOS requires Local Network permission and a declared Bonjour service. See [TN3179: Local network privacy](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy). |
| Use Bluetooth LE as a custom companion transport | **3** | CoreBluetooth can exchange custom GATT data, but BLE is low bandwidth and has strict background behavior. Use it for presence, wake-up hints, and small control frames—not message/photo bulk transfer. See [Core Bluetooth](https://developer.apple.com/documentation/corebluetooth) and Apple's [BLE transfer sample](https://developer.apple.com/documentation/corebluetooth/transferring-data-between-bluetooth-low-energy-devices). |
| Automatically reconnect whenever the devices are nearby | **2** | Windows can run a tray/background process and reconnect. iOS normally suspends apps; BLE background modes/state restoration can wake for specific events, while arbitrary always-on local-network serving is not guaranteed. Reconnect is best-effort and must tolerate delayed iOS wake-up. See [Core Bluetooth background processing](https://developer.apple.com/library/archive/documentation/NetworkingInternetWeb/Conceptual/CoreBluetooth_concepts/CoreBluetoothBackgroundProcessingForIOSApps/PerformingTasksWhileYourAppIsInTheBackground.html) and [Background Tasks](https://developer.apple.com/documentation/backgroundtasks). |
| Read user-selected or all permitted iPhone contacts | **3** | `CNContactStore` supports user-authorized limited or full contact access. iOS 18+ can grant only selected contacts. See [Accessing the contact store](https://developer.apple.com/documentation/contacts/accessing-the-contact-store). |
| Keep permitted contacts synchronized | **3** | Observe `CNContactStoreDidChange`, refetch permitted contacts, and send idempotent deltas when the companion gets execution time. Revoked contacts must be removed or tombstoned on Windows according to the retention setting. See [CNContactStore](https://developer.apple.com/documentation/contacts/cncontactstore/). |
| Search synchronized contacts | **1** | Search the authorized local replica on Windows; no live iPhone query is required after synchronization. |
| Obtain contacts without the companion via Bluetooth PBAP | **2** | iPhone supports PBAP 1.2, including phone book/favorites and call history for compatible accessories; Windows publicly supports PBAP RFCOMM service names. The user must pair and enable contact sync, and the exact fields are accessory/profile dependent. See Apple's [supported Bluetooth profiles](https://support.apple.com/en-us/102842) and Microsoft's [Bluetooth device capability declarations](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/how-to-specify-device-capabilities-for-bluetooth). |
| Read the iPhone Messages/iMessage database or complete historical threads | **4** | No iOS public API grants a third-party app access to Apple's Messages database or a user's complete iMessage/SMS history. MessageUI only presents Apple's compose UI; the Messages framework provides iMessage extensions/default-app integration, not Apple Messages database access. See [Message UI](https://developer.apple.com/documentation/messageui) and [Messages](https://developer.apple.com/documentation/messages). |
| Import old iMessage conversations or attachments | **4** | Neither the companion nor the Windows app may read the private Messages store. A user-provided export could be imported later as an explicit file-import feature, but the app cannot create that export itself through iOS public APIs. |
| Observe new message traffic while Bluetooth-connected | **2** | iPhone supports Bluetooth MAP 1.4 for incoming-message notification, inbox browsing, reply, new-message compose, and marking read on compatible connected accessories. This is not full history and must pass a Windows+iPhone profile proof before it becomes a committed feature. See Apple's [MAP support list](https://support.apple.com/en-us/102842). |
| Reply to a recent message from Windows over Bluetooth MAP | **2** | Public Bluetooth MAP defines a legitimate path, but behavior is connection-, iOS-, Windows-stack-, and profile-dependent. It must remain behind runtime capability detection. Group chats and media cannot be promised. Microsoft's own iPhone integration documents the same session-only behavior and lack of group/media support; see [Phone Link iPhone message limitations](https://support.microsoft.com/en-US/Windows/Apps/PhoneLink/send-and-receive-text-messages-from-your-pc). This demonstrates platform behavior, not an API we can call. |
| Start a new message from Windows over Bluetooth MAP | **2** | Apple's MAP support includes composing a new message, but Windows exposes generic RFCOMM rather than a high-level MAP client. We may implement standards-based OBEX/MAP only if the service is publicly enumerable on the paired iPhone and not reserved by Windows. See [Windows Bluetooth RFCOMM](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/send-or-receive-files-with-rfcomm). |
| Start an SMS/MMS from the companion globally | **3** | `MFMessageComposeViewController` can prefill recipient/body and present Apple's UI. The user must review and press Send on iPhone; the app cannot silently send or confirm delivery. See [MFMessageComposeViewController](https://developer.apple.com/documentation/messageui/mfmessagecomposeviewcontroller). |
| Silently send arbitrary SMS from the companion globally | **4** | General-purpose apps cannot programmatically send arbitrary carrier messages without user approval. Apple's Critical Messaging entitlement is for narrow safety/critical use cases and is not appropriate for a personal messaging client. See [Critical Messaging API](https://developer.apple.com/documentation/messages/critical-messaging-api). |
| Read, send, or impersonate Apple iMessage directly | **4** | There is no public iMessage transport API for a third-party Windows/iOS client and no permission that exposes Apple Messages history. Becoming the default internet messaging app only routes `im:` links to the app's own messaging service; it does not turn that app into an iMessage client. See [Preparing a default messaging app](https://developer.apple.com/documentation/messages/preparing-your-app-to-be-the-default-messaging-app). |
| Provide a private cross-device chat owned by this product | **3** | Fully legitimate as app-owned data: the companion and Windows app can exchange end-to-end encrypted messages/attachments. It will be clearly labeled as Phone Link app messaging, not iMessage/SMS. |
| Forward all iPhone notifications through the companion app | **4** | UserNotifications lets an app manage notifications delivered to its own bundle. A notification service extension modifies that app's own eligible remote notifications; it cannot enumerate other apps' or system notifications. See [User Notifications](https://developer.apple.com/documentation/usernotifications) and [UNNotificationServiceExtension](https://developer.apple.com/documentation/usernotifications/unnotificationserviceextension). |
| Receive iPhone system/app notifications through Bluetooth ANCS | **2** | Apple publishes ANCS for authorized BLE accessories. It reports notification add/modify/remove events and can expose app/title/subtitle/message attributes. It is not guaranteed to be present and data is valid only for the current session. See the [ANCS specification](https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleNotificationCenterServiceSpecification/Specification/Specification.html). |
| Perform actions on a forwarded iPhone notification | **2** | ANCS permits only the positive/negative actions iOS exposes for that notification. The Windows app must display Apple's supplied action labels and cannot invent actions or assume their result. ANCS does not provide arbitrary reply text entry. |
| Dismiss a notification on both devices | **2** | ANCS reports removal and may expose a negative action, but there is no universal "dismiss" command for every notification. Local Windows dismissal is always possible; iPhone dismissal depends on the action iOS exposes. |
| Show the caller/contact name and number on Windows | **2** | Combine authorized contacts/PBAP data with ANCS incoming-call metadata when available. Bluetooth HFP/PBAP can expose caller/call-history information to compatible accessories, but exact fields and lock-screen privacy settings vary. |
| Show iPhone call history through the companion globally | **4** | A normal iOS app has no public API for the system cellular call-history database. PBAP may expose call history to a paired Bluetooth accessory; the EU default-dialer route below has a separate, limited history API. |
| Initiate a cellular call from Windows with confirmation on iPhone | **3** | Send a signed dial request to the foreground companion and open the system telephone URL/UI. The user confirms on iPhone. This is click-to-call, not remote call control. |
| Make/receive ordinary iPhone cellular calls with audio fully inside this custom Windows app | **4** | iPhone supports HFP 1.8 and Microsoft's Phone Link can use it, but Microsoft does not expose Phone Link as a reusable SDK. Public Windows RFCOMM app capability documentation explicitly does not support headset/HFP service IDs, while phone transport/call-history APIs require restricted capabilities. A generally distributable WinUI app therefore cannot promise Phone Link-equivalent cellular call control/audio. See Apple's [HFP support](https://support.apple.com/en-us/102842), Microsoft's [RFCOMM capability limits](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/how-to-specify-device-capabilities-for-bluetooth), and [Windows app capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations). |
| Answer/decline an incoming call through ANCS | **2** | ANCS says an incoming-call notification's positive/negative actions may answer/decline. Availability is determined by iOS per notification; audio remains on iPhone or an OS-managed Bluetooth route, not our companion transport. |
| Implement app-owned VoIP calling | **3** | CallKit/LiveCommunicationKit can integrate calls supplied by our own VoIP service. This requires a real VoIP backend and does not provide access to the user's carrier calls, FaceTime, or iMessage. See [Making and receiving VoIP calls](https://developer.apple.com/documentation/callkit/making-and-receiving-voip-calls) and [LiveCommunicationKit](https://developer.apple.com/documentation/livecommunicationkit). |
| Synchronize photos/files selected by the user | **3** | A companion can send files it owns or items explicitly selected/authorized through public pickers/PhotoKit. It cannot extract attachments from private iMessage history. |
| Report iPhone device/battery/connection status | **3** | The companion can report public device/app state and battery state while executing. Background values may become stale, so every field needs an observation timestamp. |
| Revoke a paired computer | **3** | The companion can delete the trusted peer and its Keychain material; Windows deletes its protected secret and local cached data according to the user's retention choice. Bluetooth system pairing is separate and may also need removal in Settings. |
| Windows Hello/inactivity lock | **1** | Protect app access with Windows Hello/user verification where available. Phone Link leaves the option disabled when Hello is unavailable rather than creating a weaker app PIN. This protects the UI/cache; it does not add iPhone permissions. |
| Build and sign the native SwiftUI companion on this Windows PC | **4** | Swift source and an Xcode project can be authored here, but Apple's supported Xcode/iOS SDK toolchain runs on macOS. A Mac or hosted macOS CI with Xcode and the user's signing team is required to compile, install, and verify the iPhone app. See [Xcode system requirements](https://developer.apple.com/xcode/system-requirements/) and [Apple developer account/on-device testing](https://developer.apple.com/help/account/basics/about-your-developer-account). |

## EU-only iOS 26 capabilities

These are optional enhancements, not dependencies of the global product.

| Feature | Category | Boundary |
|---|---:|---|
| Send and receive SMS, MMS, and RCS in the companion | **3** | TelephonyMessagingKit works when the user has an EU-registered Apple Account, the device is physically in the EU, the entitlement is present, and the companion is selected as the default carrier messaging app. It receives messages while it is the default and can send through eligible carrier services. See [Creating a carrier messaging app](https://developer.apple.com/documentation/availability/creating-a-carrier-messaging-app) and [TelephonyMessagingKit](https://developer.apple.com/documentation/telephonymessagingkit). |
| Import carrier-message history from before the app became default | **4** | The documented API delivers incoming/queued messages to the configured default app; it does not grant the existing Apple Messages database. Queued messages may expire if not collected in time. |
| Become the default dialer and start cellular calls without confirmation | **3** | LiveCommunicationKit's default-dialer entitlement is EU-gated. When configured as default, the app can initiate cellular conversations without the confirmation dialog. See [Preparing a default dialer app](https://developer.apple.com/documentation/livecommunicationkit/preparing-your-app-to-be-the-default-dialer-app). |
| Read recent cellular call history | **3** | The EU default dialer can access conversation history only from the moment it became default. It does not expose older system history. |
| Gain iMessage access by becoming a default messaging/carrier app | **4** | Default carrier messaging covers SMS/MMS/RCS. Default internet messaging routes to the app's own service. Neither grants Apple's iMessage transport or history. |

## What Microsoft Phone Link proves—and what it does not

Microsoft documents that its iPhone path uses Bluetooth pairing and the iPhone toggles **Share System Notifications**, **Show Notifications**, and **Sync Contacts**. It also documents that iPhone message history is only the messages observed while Bluetooth is connected, is removed on disconnect, and does not support group messages or media. See [Phone Link requirements](https://support.microsoft.com/en-us/windows/apps/phonelink/phone-link-requirements-and-setup) and [iPhone message limitations](https://support.microsoft.com/en-US/Windows/Apps/PhoneLink/send-and-receive-text-messages-from-your-pc).

This confirms that the Bluetooth accessory route is real. It does **not** make Phone Link's internal Windows services a public API. Our app must use documented GATT/RFCOMM APIs and published profile specifications, and must disable a feature if Windows reserves the required profile.

## Product decision

The legitimate product is a hybrid with three capability tiers:

1. **Guaranteed core** — polished Windows UI, local database/search, secure QR trust, encrypted local sync, user-authorized contacts, app-owned messages/files/events, Windows notifications, device management, reconnect, locking, revocation, and logging.
2. **Bluetooth accessory extensions** — ANCS notifications, PBAP contacts/call history, and MAP session messaging. These are optional modules behind runtime probes and explicit user consent.
3. **Regional/entitlement extensions** — EU TelephonyMessagingKit and default-dialer capabilities when Apple reports them configured and available.

Direct iMessage access, private Messages history, cross-app notification access through the companion, and guaranteed full cellular-call audio/control in a normal third-party Windows app are out of scope because public APIs do not provide them.

## Required hardware proofs before those modules enter the committed roadmap

The following tests use the user's actual Windows Bluetooth adapter and iPhone. A failed proof disables only that module; it does not block the companion-app core.

1. **ANCS proof:** pair through Windows, enumerate the ANCS GATT service, authorize it, receive an event, fetch attributes, and verify session cleanup.
2. **PBAP proof:** enumerate the paired iPhone's PBAP server through public RFCOMM APIs and fetch a minimal phone book after the user enables contact sync.
3. **MAP proof:** enumerate the MAP message-access service, perform a read-only folder/inbox query, then separately test a user-approved send/reply. No private Windows service may be called.
4. **Call proof:** inspect only documented Windows call/Bluetooth interfaces. If HFP call control requires a restricted/private Phone Link surface, the feature remains click-to-call plus ANCS caller alerts.

## Phase 1 exit criteria

- The public API boundary is explicit.
- Every requested major feature has one of the four categories.
- Bluetooth profiles are treated as optional capability-tested integrations, not promises.
- Global and EU-only designs are separated.
- No implementation depends on Apple Account credentials or access to Apple's private Messages services.
- Phase 2 can now design the architecture against this boundary.
