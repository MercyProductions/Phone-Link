# Pair without a camera scan

This uses the same offer, pinned TLS, one-attempt expiry, and six-word confirmation as scanning. It transfers only the offer file over USB; the authenticated companion session still needs an eligible local network shared with the PC.

1. On Windows, open Apple Devices and select the unlocked USB-connected iPhone. Open **Files** and locate the updated **Phone Link** companion.
2. In Phone Link for Windows, choose **Connected devices → Pair iPhone → Save pairing file instead**. Save the `.plpair` file in a private local folder. The offer expires two minutes after creation; open Apple Devices first so the transfer can fit in that window.
3. Drag the file into **Phone Link** in Apple Devices. In the companion, choose **Pair with Windows → Open pairing file**, then select it under **On My iPhone → Phone Link**.
4. Tap **Connect securely** and confirm on both devices only if all six words match. Keep the file private and delete both copies after use; the app does not delete a user-selected file.

The companion exposes its Documents folder for this file handoff. It does not store identities, databases, diagnostics, or private application state there. The imported file is size-bounded and decoded with the existing strict pairing codec; it never creates trust by itself. Saving a file does not extend expiry, reset trust, or bypass network or USB authorization. The file path does not require the iPhone Camera or camera permission.

References: [Apple Devices file transfer](https://support.apple.com/guide/devices-windows/transfer-files-between-your-devices-mchl4bd77d3a/windows) and [Apple file-provider documentation](https://developer.apple.com/documentation/fileprovider).
