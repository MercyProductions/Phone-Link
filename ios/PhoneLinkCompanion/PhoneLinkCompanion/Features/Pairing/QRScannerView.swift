import SwiftUI
import VisionKit

struct QRScannerView: UIViewControllerRepresentable {
    let onResult: (Result<String, Error>) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(onResult: onResult)
    }

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced,
            recognizesMultipleItems: false,
            isHighFrameRateTrackingEnabled: false,
            isPinchToZoomEnabled: true,
            isGuidanceEnabled: true,
            isHighlightingEnabled: true
        )
        scanner.delegate = context.coordinator

        do {
            try scanner.startScanning()
        } catch {
            onResult(.failure(error))
        }
        return scanner
    }

    func updateUIViewController(_ uiViewController: DataScannerViewController, context: Context) {}

    static func dismantleUIViewController(_ uiViewController: DataScannerViewController, coordinator: Coordinator) {
        uiViewController.stopScanning()
    }

    @MainActor
    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        private let onResult: (Result<String, Error>) -> Void
        private var hasDeliveredResult = false

        init(onResult: @escaping (Result<String, Error>) -> Void) {
            self.onResult = onResult
        }

        func dataScanner(
            _ dataScanner: DataScannerViewController,
            didAdd addedItems: [RecognizedItem],
            allItems: [RecognizedItem]
        ) {
            guard !hasDeliveredResult else { return }
            guard case .barcode(let barcode) = addedItems.first,
                  let payload = barcode.payloadStringValue else { return }

            hasDeliveredResult = true
            dataScanner.stopScanning()
            onResult(.success(payload))
        }
    }
}
