import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class ReviewStoreTests: XCTestCase {
    func testRefreshingIdentitiesInvalidatesTheLoaderWithoutSavingSettingsOrDiscardingTheSnapshot() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let snapshot = ReviewSnapshot(pullRequests: [], updatedAt: Date(timeIntervalSince1970: 1))
        let loader = StubReviewLoader([.success(snapshot), .failure(.http(403, "example/Project"))])
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        var saved = configuration()
        saved.refreshInterval = .fiveMinutes
        try store.save(saved)
        let encoded = defaults.data(forKey: ReviewStore.settingsKey)
        await store.refresh()
        await store.refreshIdentities()
        let invalidations = await loader.identityInvalidations
        let calls = await loader.calls
        XCTAssertEqual(invalidations, 1)
        XCTAssertEqual(calls, 2)
        XCTAssertEqual(store.snapshot, snapshot)
        XCTAssertNotNil(store.errorMessage)
        XCTAssertEqual(store.configuration, saved)
        XCTAssertEqual(defaults.data(forKey: ReviewStore.settingsKey), encoded)
    }

    private func preferences() throws -> (UserDefaults, String) {
        let name = "ReviewQ-tests-\(UUID().uuidString)"
        return (try XCTUnwrap(UserDefaults(suiteName: name)), name)
    }

    func testFirstRunDoesNotFetchOrShowAnInventedZero() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let loader = StubReviewLoader([])
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        await store.refresh()
        XCTAssertNil(store.snapshot)
        XCTAssertNil(store.errorMessage)
        XCTAssertEqual(store.menuBarTitle, "")
        let calls = await loader.calls
        XCTAssertEqual(calls, 0)
    }

    func testFailurePreservesLastSuccessfulSnapshotAndMarksCountStale() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let lastSuccess = ReviewSnapshot(pullRequests: [], updatedAt: Date(timeIntervalSince1970: 1))
        let loader = StubReviewLoader([.success(lastSuccess), .failure(.http(403, "example/Project"))])
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        try store.save(configuration())
        await store.refresh()
        XCTAssertEqual(store.menuBarTitle, " 0")
        await store.refresh()
        XCTAssertEqual(store.snapshot, lastSuccess)
        XCTAssertNotNil(store.errorMessage)
        XCTAssertEqual(store.menuBarTitle, " 0 !")
    }

    func testFailureBeforeAnySuccessShowsErrorNotZero() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let store = ReviewStore(
            defaults: defaults, keychain: StubPATStore(),
            loader: StubReviewLoader([.failure(.http(401, "example/Project"))])
        )
        try store.save(configuration())
        await store.refresh()
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.menuBarTitle, " !")
    }

    func testSavingPreferencesPersistsConfigurationButNeverToken() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        var settings = configuration()
        settings.authentication = .personalAccessToken
        settings.includeDrafts = true
        settings.showCount = false
        settings.scoutAdditionalInstructions = "Use Scout's review skill.\nCheck correctness."
        settings.copilotAdditionalInstructions = "Use Copilot's review skill.\nCheck regressions."
        settings.followPeople = true
        settings.followedAuthorEmails = "alex@example.com;sam@example.com"
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: StubReviewLoader([]))
        try store.save(settings, newPAT: "synthetic-token")
        let data = try XCTUnwrap(defaults.data(forKey: ReviewStore.settingsKey))
        XCTAssertFalse(try XCTUnwrap(String(data: data, encoding: .utf8)).contains("synthetic-token"))
        let restored = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: StubReviewLoader([]))
        XCTAssertEqual(restored.configuration, settings)
    }

    func testInvalidPromptsCannotReplacePreferencesSnapshotOrPAT() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let keychain = StubPATStore(token: "original-token", onSave: { _ in
            XCTFail("Invalid prompts must be rejected before the saved PAT can be changed.")
        })
        let snapshot = ReviewSnapshot(pullRequests: [])
        let store = ReviewStore(defaults: defaults, keychain: keychain, loader: StubReviewLoader([.success(snapshot)]))
        var original = configuration()
        original.authentication = .personalAccessToken
        original.scoutAdditionalInstructions = "Original Scout instructions."
        original.copilotAdditionalInstructions = "Original Copilot instructions."
        try store.save(original)
        await store.refresh()
        let saved = try XCTUnwrap(defaults.data(forKey: ReviewStore.settingsKey))
        for assistant in 0..<2 {
            var invalid = original
            if assistant == 0 { invalid.scoutAdditionalInstructions = String(repeating: "x", count: 1_001) }
            else { invalid.copilotAdditionalInstructions = "Not allowed\tinput" }
            XCTAssertThrowsError(try store.save(invalid, newPAT: "replacement-token"))
            XCTAssertEqual(store.configuration, original)
            XCTAssertEqual(defaults.data(forKey: ReviewStore.settingsKey), saved)
            XCTAssertEqual(store.snapshot, snapshot)
            XCTAssertEqual(keychain.load(), "original-token")
        }
    }

    func testInvalidStoredPromptsAreVisibleEvenWithoutConfiguredSources() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        var settings = ReviewConfiguration()
        settings.copilotAdditionalInstructions = String(repeating: "x", count: 1_001)
        defaults.set(try JSONEncoder().encode(settings), forKey: ReviewStore.settingsKey)
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: StubReviewLoader([]))
        XCTAssertEqual(store.errorMessage, ReviewQError.reviewPromptTooLong.localizedDescription)
        XCTAssertEqual(store.menuBarTitle, " !")
        XCTAssertEqual(store.configuration.copilotAdditionalInstructions, settings.copilotAdditionalInstructions)
    }

    func testInvalidFollowSettingsCannotReplacePreferencesSnapshotOrPAT() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let keychain = StubPATStore(onSave: { _ in
            XCTFail("Invalid followed emails must be rejected before the saved PAT can be changed.")
        })
        let snapshot = ReviewSnapshot(pullRequests: [])
        let store = ReviewStore(defaults: defaults, keychain: keychain, loader: StubReviewLoader([.success(snapshot)]))
        var original = configuration()
        original.authentication = .personalAccessToken
        try store.save(original)
        await store.refresh()
        let saved = try XCTUnwrap(defaults.data(forKey: ReviewStore.settingsKey))
        var invalid = original
        invalid.followPeople = true
        invalid.followedAuthorEmails = "alex@example.com; sam@example.com"
        XCTAssertThrowsError(try store.save(invalid, newPAT: "replacement-token"))
        XCTAssertEqual(store.configuration, original)
        XCTAssertEqual(defaults.data(forKey: ReviewStore.settingsKey), saved)
        XCTAssertEqual(store.snapshot, snapshot)
        invalid.sourceURLs = ""
        defaults.set(try JSONEncoder().encode(invalid), forKey: ReviewStore.settingsKey)
        let restored = ReviewStore(defaults: defaults, keychain: keychain, loader: StubReviewLoader([]))
        XCTAssertEqual(restored.errorMessage, ReviewQError.invalidFollowedAuthorEmails.localizedDescription)
        XCTAssertEqual(restored.configuration.followedAuthorEmails, invalid.followedAuthorEmails)
    }

    func testInvalidSettingsCannotReplaceSavedConfiguration() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: StubReviewLoader([]))
        let original = configuration()
        try store.save(original)
        XCTAssertThrowsError(try store.save(configuration("https://evil.test/Project")))
        XCTAssertEqual(store.configuration, original)
    }

    func testCorruptPersistedSettingsAreVisible() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(Data("broken".utf8), forKey: ReviewStore.settingsKey)
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: StubReviewLoader([]))
        XCTAssertNotNil(store.errorMessage)
        XCTAssertEqual(store.menuBarTitle, " !")
    }

    func testDeletingActivePATClearsTheQueueAndSurfacesMissingCredential() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let store = ReviewStore(
            defaults: defaults, keychain: StubPATStore(),
            loader: StubReviewLoader([.success(ReviewSnapshot(pullRequests: []))])
        )
        var settings = configuration()
        settings.authentication = .personalAccessToken
        try store.save(settings)
        await store.refresh()
        XCTAssertNotNil(store.snapshot)
        try store.deleteSavedPAT()
        XCTAssertNil(store.snapshot)
        XCTAssertNotNil(store.errorMessage)
    }

    func testChangingSourcesRejectsAnOldInFlightResponse() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let loader = DeferredReviewLoader()
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        try store.save(configuration())
        let refresh = Task { await store.refresh() }
        await loader.waitUntilStarted()
        let replacement = configuration("https://dev.azure.com/another/Project")
        try store.save(replacement)
        await loader.finish(ReviewSnapshot(pullRequests: []))
        await refresh.value
        XCTAssertEqual(store.configuration, replacement)
        XCTAssertNil(store.snapshot)
        XCTAssertNil(store.errorMessage)
        XCTAssertFalse(store.isRefreshing)
        XCTAssertEqual(store.menuBarTitle, " ...")
    }
}
