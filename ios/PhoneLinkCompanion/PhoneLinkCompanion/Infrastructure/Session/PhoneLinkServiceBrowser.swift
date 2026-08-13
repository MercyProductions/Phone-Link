import Foundation
import Network

@MainActor
final class PhoneLinkServiceBrowser {
    private struct Service: Hashable {
        let name: String
        let type: String
        let domain: String
    }

    private var browser: NWBrowser?
    private var services: Set<Service> = []
    private var browseContinuation: CheckedContinuation<[Service], Never>?

    func discoverEndpoints(timeout: TimeInterval = 2.5) async -> [PairingEndpoint] {
        let foundServices = await browse(timeout: timeout)
        var endpoints: [PairingEndpoint] = []
        for service in foundServices.prefix(16) {
            do {
                endpoints.append(try await NetServiceResolver.resolve(
                    name: service.name,
                    type: service.type,
                    domain: service.domain,
                    timeout: timeout
                ))
            } catch {
                continue
            }
        }
        return endpoints
    }

    private func browse(timeout: TimeInterval) async -> [Service] {
        finishBrowse()
        services.removeAll()

        return await withCheckedContinuation { continuation in
            browseContinuation = continuation
            let parameters = NWParameters.tcp
            parameters.includePeerToPeer = true
            let browser = NWBrowser(
                for: .bonjour(type: "_phonelink._tcp", domain: "local."),
                using: parameters
            )
            self.browser = browser
            browser.browseResultsChangedHandler = { [weak self] results, _ in
                Task { @MainActor in
                    guard let self else { return }
                    self.services = Set(results.compactMap { result in
                        guard case let .service(name, type, domain, _) = result.endpoint else {
                            return nil
                        }
                        return Service(name: name, type: type, domain: domain)
                    })
                }
            }
            browser.stateUpdateHandler = { [weak self] state in
                if case .failed = state {
                    Task { @MainActor in self?.finishBrowse() }
                }
            }
            browser.start(queue: .main)
            DispatchQueue.main.asyncAfter(deadline: .now() + timeout) { [weak self] in
                self?.finishBrowse()
            }
        }
    }

    private func finishBrowse() {
        browser?.cancel()
        browser = nil
        if let continuation = browseContinuation {
            browseContinuation = nil
            continuation.resume(returning: services.sorted { $0.name < $1.name })
        }
    }
}

private final class NetServiceResolver: NSObject, NetServiceDelegate {
    private let service: NetService
    private var continuation: CheckedContinuation<PairingEndpoint, Error>?
    private var retainedSelf: NetServiceResolver?

    private init(name: String, type: String, domain: String) {
        service = NetService(domain: domain, type: type, name: name)
        super.init()
        service.delegate = self
    }

    @MainActor
    static func resolve(
        name: String,
        type: String,
        domain: String,
        timeout: TimeInterval
    ) async throws -> PairingEndpoint {
        let resolver = NetServiceResolver(name: name, type: type, domain: domain)
        return try await withCheckedThrowingContinuation { continuation in
            resolver.retainedSelf = resolver
            resolver.continuation = continuation
            resolver.service.resolve(withTimeout: timeout)
        }
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard let host = sender.hostName?.trimmingCharacters(in: CharacterSet(charactersIn: ".")),
              let port = UInt16(exactly: sender.port) else {
            finish(.failure(BonjourDiscoveryError.invalidService))
            return
        }
        finish(.success(PairingEndpoint(host: host, port: port)))
    }

    func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) {
        finish(.failure(BonjourDiscoveryError.resolutionFailed))
    }

    private func finish(_ result: Result<PairingEndpoint, Error>) {
        service.stop()
        service.delegate = nil
        guard let continuation else { return }
        self.continuation = nil
        retainedSelf = nil
        continuation.resume(with: result)
    }
}

enum BonjourDiscoveryError: Error {
    case invalidService
    case resolutionFailed
}
