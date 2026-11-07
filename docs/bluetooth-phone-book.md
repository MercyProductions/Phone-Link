# Bluetooth phone book

Open **Contacts → Bluetooth phone book (read-only)**, select your paired iPhone, and choose **Read phone book**. This uses the phone's PBAP service rather than the iPhone companion. It requires Windows RFCOMM access and any contact-sharing permission the phone presents. The older companion-sync list is independent and unchanged on disk.

The connection is encrypted and reads only the main `telecom/pb.vcf` phone book, 100 entries at a time, as UTF-8 vCard 3.0. Each result is bounded to 128 KiB; the request selects name and telephone fields only. Next and Previous explicitly request another page. The preview retains up to eight unique numbers per card; email, notes, photos, SIM contacts and call-history objects are not requested or displayed. Nothing is written to the phone or merged into the saved contacts. Closing the section, changing selection, navigating away, or locking Phone Link clears the preview.

Verification: scripted exchanges validate the PBAP request target, repository, offset, and property mask; parser tests cover folded names, escaped names, grouped telephone fields, duplicate numbers, and malformed cards. Existing MAP wire tests still pass after sharing the bounded OBEX implementation. Full Windows build: zero warnings/errors; 535 .NET tests passed. The local smoke check did not identify an unambiguous paired iPhone by its displayed name, so it did not open a phone book. Select the intended phone in the app to verify a physical read. A PBAP service advertisement alone does not establish contact access.

Protocol reference: [Bluetooth SIG Phone Book Access Profile 1.2.3](https://www.bluetooth.com/specifications/specs/phone-book-access-profile-1-2-3/).
