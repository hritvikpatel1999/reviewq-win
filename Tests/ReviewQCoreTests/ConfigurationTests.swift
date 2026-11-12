import Foundation
@testable import ReviewQCore
import XCTest

final class ConfigurationTests: XCTestCase {
    func testNewConfigurationDefaultsToFifteenMinutesAndSavedFiveMinutesSurvive() throws {
        XCTAssertEqual(ReviewConfiguration().refreshInterval, .fifteenMinutes)
        var saved = configuration()
        saved.refreshInterval = .fiveMinutes
        let decoded = try JSONDecoder().decode(ReviewConfiguration.self, from: JSONEncoder().encode(saved))
        XCTAssertEqual(decoded.refreshInterval, .fiveMinutes)
        XCTAssertEqual(decoded.refreshInterval.rawValue, 300)
    }

    func testModernAndLegacySourcesNormalize() throws {
        let modern = try ReviewSource.parse("https://dev.azure.com/example/Project%20Name/")
        let legacy = try ReviewSource.parse("https://example.visualstudio.com/DefaultCollection/Project%20Name")
        XCTAssertEqual(modern, legacy)
        XCTAssertEqual(modern.project, "Project Name")
        XCTAssertEqual(try modern.webURL().absoluteString, "https://dev.azure.com/example/Project%20Name")
        XCTAssertNil(try ReviewSource.parse("https://dev.azure.com/example").project)
    }

    func testUnsafeAndUnsupportedSourcesAreRejected() {
        for value in [
            "http://dev.azure.com/example/Project",
            "https://dev.azure.com.evil.test/example/Project",
            "https://evil.test/example/Project",
            "https://dev.azure.com",
            "https://user:password@dev.azure.com/example/Project",
            "https://dev.azure.com:8443/example/Project",
            "https://dev.azure.com/example/Project?_a=review",
            "https://dev.azure.com/example/Project#fragment",
            "https://dev.azure.com/example/Project/_git/Repository",
            "https://example.other.visualstudio.com/Project",
            "https://dev.azure.com/example/..",
            "https://dev.azure.com/example/%2E%2E",
            "https://dev.azure.com/example/_pulls",
            "https://dev.azure.com/example/Project%0AName",
            "https://dev.azure.com/example/Project%5CName"
        ] {
            XCTAssertThrowsError(try ReviewSource.parse(value), value)
        }
    }

    func testDuplicateSourcesAndMissingSourcesAreExplicitErrors() {
        XCTAssertThrowsError(try configuration(" \n ").validatedSources())
        XCTAssertThrowsError(try configuration("""
        https://dev.azure.com/example/Project
        https://EXAMPLE.visualstudio.com/DefaultCollection/project
        """).validatedSources())
    }

    func testMultipleSourcesAndTenantValidation() throws {
        var settings = configuration("""
        https://dev.azure.com/example/Project

        https://dev.azure.com/another
        """)
        settings.tenantID = "example.onmicrosoft.com"
        XCTAssertEqual(try settings.validatedSources().count, 2)
        settings.tenantID = "--help; echo bad"
        XCTAssertThrowsError(try settings.validatedSources())
        settings.tenantID = reviewerID
        XCTAssertEqual(try settings.validatedSources().count, 2)
    }

    func testPATValidationDoesNotAcceptHeadersOrWhitespace() throws {
        XCTAssertEqual(try KeychainPATStore.validate(" synthetic-token\n"), "synthetic-token")
        for value in ["", "Basic token", "Bearer token", "abc\ndef", "abc\tdef", "abc\u{0}def"] {
            XCTAssertThrowsError(try KeychainPATStore.validate(value), value)
        }
    }

    func testConfigurationNeverContainsPAT() throws {
        var settings = configuration()
        settings.authentication = .personalAccessToken
        let encoded = try JSONEncoder().encode(settings)
        let text = try XCTUnwrap(String(data: encoded, encoding: .utf8))
        XCTAssertFalse(text.contains("synthetic-token"))
        XCTAssertEqual(try JSONDecoder().decode(ReviewConfiguration.self, from: encoded), settings)
    }

    func testLegacySettingsPreserveEveryExistingValueAndDefaultOnlyMissingPromptKeys() throws {
        var original = configuration()
        original.tenantID = "example.onmicrosoft.com"
        original.refreshInterval = .thirtyMinutes
        original.includeDrafts = true
        original.showCount = false
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as? [String: Any]
        )
        object.removeValue(forKey: "scoutAdditionalInstructions")
        object.removeValue(forKey: "copilotAdditionalInstructions")
        let legacy = try JSONSerialization.data(withJSONObject: object)
        XCTAssertEqual(try JSONDecoder().decode(ReviewConfiguration.self, from: legacy), original)

        object["scoutAdditionalInstructions"] = "Use a Scout skill."
        original.scoutAdditionalInstructions = "Use a Scout skill."
        XCTAssertEqual(
            try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: object)),
            original
        )
        for invalid: Any in [NSNull(), 123] {
            object["scoutAdditionalInstructions"] = invalid
            XCTAssertThrowsError(
                try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: object))
            )
        }
        object["scoutAdditionalInstructions"] = ""
        object.removeValue(forKey: "authentication")
        XCTAssertThrowsError(
            try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: object))
        )
    }

    func testIndependentInstructionsRoundTripWithoutChangingTheirContents() throws {
        var settings = configuration()
        settings.scoutAdditionalInstructions = "Use the Scout skill.\nFocus on correctness."
        settings.copilotAdditionalInstructions = "Use the Copilot skill.\nFocus on regressions."
        let restored = try JSONDecoder().decode(ReviewConfiguration.self, from: JSONEncoder().encode(settings))
        XCTAssertEqual(restored, settings)
        XCTAssertNotEqual(restored.scoutAdditionalInstructions, restored.copilotAdditionalInstructions)
    }

    func testExactlyOneThousandCharactersAreAcceptedForEachAppAndOneMoreIsRejected() throws {
        for text in ["x", "\u{1F469}\u{1F3FD}\u{200D}\u{1F4BB}"] {
            var settings = configuration()
            let maximum = String(repeating: text, count: 1_000)
            XCTAssertEqual(maximum.count, 1_000)
            settings.scoutAdditionalInstructions = maximum
            settings.copilotAdditionalInstructions = maximum
            XCTAssertNoThrow(try settings.validatedReviewPrompts())
            settings.scoutAdditionalInstructions += text
            XCTAssertThrowsError(try settings.validatedReviewPrompts())
            settings.scoutAdditionalInstructions = maximum
            settings.copilotAdditionalInstructions += text
            XCTAssertThrowsError(try settings.validatedSources())
        }
    }

    func testMultilineInstructionsSupportUnicodeButRejectUnsafeControlInput() throws {
        try ReviewConfiguration.validateAdditionalInstructions("Line one\nLine two\r\nCaf\u{e9} \u{1F600}")
        for invalid in ["a\tb", "a\u{0}b", "a\u{1b}b", "a\u{7f}b"] {
            XCTAssertThrowsError(try ReviewConfiguration.validateAdditionalInstructions(invalid)) {
                XCTAssertEqual($0.localizedDescription, ReviewQError.invalidReviewPrompt.localizedDescription)
            }
        }
        XCTAssertEqual(DiagnosticFailure(ReviewQError.reviewPromptTooLong).reason, .invalidConfiguration)
        XCTAssertEqual(DiagnosticFailure(ReviewQError.invalidReviewPrompt).reason, .invalidConfiguration)
    }

    func testFollowingDefaultsOffAndMigratesMissingKeysWithoutChangingExistingSettings() throws {
        var original = configuration()
        original.scoutAdditionalInstructions = "Keep these instructions."
        original.includeDrafts = true
        XCTAssertFalse(original.followPeople)
        XCTAssertEqual(try original.validatedFollowedAuthors(), [])
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as? [String: Any]
        )
        object.removeValue(forKey: "followPeople")
        object.removeValue(forKey: "followedAuthorEmails")
        XCTAssertEqual(
            try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: object)),
            original
        )
        let invalidValues: [(String, Any)] = [
            ("followPeople", "true"), ("followPeople", NSNull()),
            ("followedAuthorEmails", true), ("followedAuthorEmails", NSNull())
        ]
        for (key, value) in invalidValues {
            var invalid = object
            invalid[key] = value
            XCTAssertThrowsError(
                try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: invalid))
            )
        }
    }

    func testFollowedEmailsAreSemicolonSeparatedCaseInsensitiveAndDeduplicated() throws {
        var settings = configuration()
        settings.followPeople = true
        settings.followedAuthorEmails = "Alex+reviews@Example.com;sam@example.co.uk;ALEX+REVIEWS@example.com"
        XCTAssertEqual(try settings.validatedFollowedAuthors(), ["alex+reviews@example.com", "sam@example.co.uk"])
        let restored = try JSONDecoder().decode(ReviewConfiguration.self, from: JSONEncoder().encode(settings))
        XCTAssertEqual(restored, settings, "Preserve the user's textbox contents while normalizing lookup keys.")
    }

    func testIdentityEmailLookupEncodesPlusLiterallyInsteadOfAsAFormSpace() throws {
        let url = try AzureDevOpsURL.make(
            organization: "example", path: ["_apis", "identities"],
            query: [URLQueryItem(name: "filterValue", value: "alex+reviews@example.com")],
            identityService: true
        )
        XCTAssertEqual(url.host, "vssps.dev.azure.com")
        XCTAssertTrue(url.absoluteString.contains("alex%2Breviews"))
        let query = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        XCTAssertEqual(query.first?.value, "alex+reviews@example.com")
    }

    func testInvalidFollowedEmailListsAreRejectedOnlyWhenEnabled() throws {
        for text in [
            "", " ", "alex@example.com; sam@example.com", "alex@example.com;",
            ";alex@example.com", "alex@example.com;;sam@example.com",
            "alex@example.com,sam@example.com", "Alex <alex@example.com>",
            "alex@example.com\n", "alex\n@example.com", "alex@example.com\t",
            "alex\u{a0}@example.com", "alex@\u{0}example.com",
            "alex", "@example.com", "alex@", "alex@@example.com",
            "alex@localhost", ".alex@example.com", "alex.@example.com",
            "al..ex@example.com", "alex@-example.com", "alex@example-.com",
            "\(String(repeating: "a", count: 65))@example.com",
            "alex@\(String(repeating: "a", count: 64)).com"
        ] {
            var settings = configuration()
            settings.followedAuthorEmails = text
            XCTAssertEqual(try settings.validatedFollowedAuthors(), [], text)
            settings.followPeople = true
            XCTAssertThrowsError(try settings.validatedSources(), text) {
                XCTAssertEqual($0.localizedDescription, ReviewQError.invalidFollowedAuthorEmails.localizedDescription)
            }
        }
        XCTAssertEqual(DiagnosticFailure(ReviewQError.invalidFollowedAuthorEmails).reason, .invalidConfiguration)
        XCTAssertEqual(DiagnosticFailure(ReviewQError.unresolvedFollowedAuthors([])).reason, .invalidConfiguration)
    }
}
