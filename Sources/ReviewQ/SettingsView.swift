import ReviewQCore
import SwiftUI

struct SettingsView: View {
    @ObservedObject var store: ReviewStore
    @ObservedObject var app: AppDelegate
    @ObservedObject var loginItem: LoginItemStore
    @ObservedObject var menuShortcut: GlobalShortcutStore
    @State private var draft: ReviewConfiguration
    @State private var token = ""
    @State private var formError: String?
    @State private var didSave = false
    @State private var confirmDeletePAT = false

    init(store: ReviewStore, app: AppDelegate, loginItem: LoginItemStore) {
        self.store = store
        self.app = app
        self.loginItem = loginItem
        menuShortcut = app.menuShortcut
        _draft = State(initialValue: store.configuration)
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    HStack(spacing: 12) {
                        Image(systemName: "arrow.triangle.pull")
                            .font(.system(size: 30)).foregroundStyle(.tint)
                        VStack(alignment: .leading, spacing: 3) {
                            Text("Azure DevOps reviews").font(.title2.weight(.semibold))
                            Text("A quiet home for your next pull request.")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    sources
                    authentication
                    FollowedPeopleSettings(isEnabled: $draft.followPeople, emails: $draft.followedAuthorEmails)
                    AuthoredPullRequestsSettings(isEnabled: $draft.showAuthoredPullRequests)
                    additionalInstructions
                    preferences
                    startup
                    identityCache
                    if let error = store.errorMessage {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .font(.caption).foregroundStyle(.red)
                    }
                    if let error = app.actionError {
                        Text(error).font(.caption).foregroundStyle(.red)
                    }
                }
                .padding(24)
            }
            Divider()
            HStack {
                Button("Diagnostics...") { app.showDiagnostics() }
                    .buttonStyle(.bordered)
                    .keyboardShortcut("d", modifiers: [.command, .shift])
                VStack(alignment: .leading, spacing: 4) {
                    if let formError {
                        Text(formError).font(.caption).foregroundStyle(.red)
                    } else if store.isRefreshing {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Refreshing reviews...").font(.caption).foregroundStyle(.secondary)
                        }
                    } else if didSave {
                        Text("Settings saved.").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                Button("Save and Refresh", action: save)
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(promptValidationError != nil || followedPeopleValidationError != nil)
            }
            .padding(16)
        }
        .frame(minWidth: 520, minHeight: 540)
        .onChange(of: draft) { _, _ in
            didSave = false
            formError = nil
        }
        .confirmationDialog("Delete the saved Azure DevOps PAT?", isPresented: $confirmDeletePAT) {
            Button("Delete saved PAT", role: .destructive) {
                do {
                    try store.deleteSavedPAT()
                    token = ""
                    formError = nil
                } catch { formError = error.localizedDescription }
            }
        } message: {
            Text("If PAT authentication is active, reviews stop loading until you save a new token.")
        }
    }

    private var sources: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 8) {
                Text("One organization or project URL per line.")
                    .font(.caption).foregroundStyle(.secondary)
                TextEditor(text: $draft.sourceURLs)
                    .font(.system(size: 12, design: .monospaced))
                    .scrollContentBackground(.hidden)
                    .frame(height: 82)
                    .padding(6)
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                    .overlay(alignment: .topLeading) {
                        if draft.sourceURLs.isEmpty {
                            Text("https://dev.azure.com/your-organization/your-project")
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundStyle(.tertiary).padding(11)
                                .allowsHitTesting(false)
                        }
                    }
                    .accessibilityLabel("Azure DevOps source URLs")
                Text("Organization-only URLs search all accessible projects. Project URLs are faster for large organizations.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Review sources", systemImage: "building.2") }
    }

    private var authentication: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 12) {
                Picker("Authentication", selection: $draft.authentication) {
                    Text("Azure CLI").tag(AuthenticationMethod.azureCLI)
                    Text("Personal access token").tag(AuthenticationMethod.personalAccessToken)
                }
                .pickerStyle(.segmented).labelsHidden()
                if draft.authentication == .azureCLI {
                    Text("Sign in once in Terminal; ReviewQ uses short-lived Entra tokens.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("az login --allow-no-subscriptions")
                        .font(.system(size: 12, design: .monospaced)).textSelection(.enabled)
                    TextField("Tenant ID or domain (optional)", text: $draft.tenantID)
                        .textFieldStyle(.roundedBorder)
                    Text("Leave the tenant blank to use your current Azure CLI account.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Azure CLI setup instructions") {
                        app.openDocumentation("https://learn.microsoft.com/azure/devops/cli/entra-tokens")
                    }
                    .buttonStyle(.link)
                } else {
                    SecureField("Paste a Code (Read) PAT", text: $token)
                        .textFieldStyle(.roundedBorder)
                        .onChange(of: token) { _, _ in formError = nil; didSave = false }
                    Text("Stored in macOS Keychain, never in settings. Leave blank to retain the saved PAT.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("Organization-wide discovery also needs Project and Team (Read). The PAT must cover every source.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("How to create a PAT") {
                        app.openDocumentation(
                            "https://learn.microsoft.com/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate"
                        )
                    }
                    .buttonStyle(.link)
                }
                Button("Delete saved PAT", role: .destructive) { confirmDeletePAT = true }
                    .font(.caption)
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Authentication", systemImage: "key") }
    }

    private var preferences: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 10) {
                Picker("Refresh", selection: $draft.refreshInterval) {
                    ForEach(RefreshInterval.allCases, id: \.self) { interval in
                        Text(interval.label).tag(interval)
                    }
                }
                Toggle("Show pending count in the menu bar", isOn: $draft.showCount)
                Toggle("Include draft pull requests", isOn: $draft.includeDrafts)
                Text("Repository sections show active, directly assigned PRs with no vote from you. Following people and authored PRs add separate optional sections.")
                    .font(.caption).foregroundStyle(.secondary)
                Divider()
                Text("Command-Option-Q opens or closes the review queue from any app.")
                    .font(.caption).foregroundStyle(.secondary)
                if let error = menuShortcut.errorMessage {
                    Text(error).font(.caption).foregroundStyle(.red)
                    Button("Retry shortcut") { app.startMenuShortcut() }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Review queue", systemImage: "line.3.horizontal.decrease.circle") }
    }

    private var promptValidationError: String? {
        do {
            try draft.validatedReviewPrompts()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    private var followedPeopleValidationError: String? {
        do {
            _ = try draft.validatedFollowedAuthors()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    private var additionalInstructions: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 12) {
                Text("Always sent first: Review PR: {PR Link}")
                    .font(.system(size: 11, design: .monospaced))
                Text("Add separate instructions after the link, such as which skill to use. Leave blank for the default prompt.")
                    .font(.caption).foregroundStyle(.secondary)
                ReviewInstructionsEditor(title: "Scout", text: $draft.scoutAdditionalInstructions)
                ReviewInstructionsEditor(title: "GitHub Copilot", text: $draft.copilotAdditionalInstructions)
                if let error = promptValidationError {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
                Text("Stored locally in preferences, not Keychain. Do not enter secrets.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Review instructions", systemImage: "text.bubble") }
    }

    private var startup: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Launch ReviewQ at login", isOn: Binding(
                    get: { loginItem.isOn },
                    set: { value in Task { await loginItem.setEnabled(value) } }
                ))
                .disabled(loginItem.isWorking || loginItem.status == .notFound)
                Text(loginItem.statusMessage).font(.caption).foregroundStyle(.secondary)
                if loginItem.status == .requiresApproval {
                    Button("Open Login Items") { loginItem.openSystemSettings() }
                }

                if let error = loginItem.errorMessage {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Startup", systemImage: "power") }
    }

    private var identityCache: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 8) {
                Text("Your user ID and resolved followed authors are cached locally for 7 days, including across app restarts. No-match results expire after 15 minutes.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("PRs, votes, and comment states are still checked on every refresh. Ordinary refreshes reuse valid identities.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Refresh identities") { Task { await store.refreshIdentities() } }
                    .disabled(store.isRefreshing || !store.configuration.hasSources)
                    .help("Clear cached identities, renew authentication, and refresh using your saved settings.")
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("Identity cache", systemImage: "person.crop.circle.badge.clock") }
    }

    private func save() {
        do {
            try store.save(draft, newPAT: token)
            token = ""
            formError = nil
            didSave = true
        } catch {
            formError = error.localizedDescription
            didSave = false
        }
    }
}

struct AuthoredPullRequestsSettings: View {
    @Binding var isEnabled: Bool

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 10) {
                Toggle("Show PRs I've authored", isOn: $isEnabled)
                    .accessibilityIdentifier("authored-pull-requests-toggle")
                Text("Off by default. Shows your active PRs across accessible projects in your configured organizations. The draft preference also applies.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Shows active discussion counts and Merge Ready when required approvals are met with no active comments. Build status is not checked. Browser links only.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Adds read-only PR, discussion, and reviewer-policy requests only while enabled. PATs need Code (Read) and Project and Team (Read).")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("I'm the Author", systemImage: "person.crop.circle") }
    }
}

struct FollowedPeopleSettings: View {
    @Binding var isEnabled: Bool
    @Binding var emails: String

    private var validationError: String? {
        guard isEnabled else { return nil }
        do {
            _ = try ReviewConfiguration.parseFollowedAuthorEmails(emails)
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 10) {
                Toggle("Follow PR authors", isOn: $isEnabled)
                    .accessibilityIdentifier("follow-people-toggle")
                TextField("alex@example.com;sam@example.com", text: $emails)
                    .textFieldStyle(.roundedBorder)
                    .disabled(!isEnabled)
                    .accessibilityLabel("Followed author emails, separated by semicolons without spaces")
                    .accessibilityIdentifier("followed-author-emails")
                Text("Enter email addresses separated by semicolons, with no spaces.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Off by default. When enabled, People you follow appears below your repositories with other active PRs by these authors across accessible projects in your configured organizations.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Adds requests only while enabled. PATs also need Identity (Read) and Project and Team (Read). Emails stay in local preferences and are sent only to Azure DevOps to find the authors.")
                    .font(.caption).foregroundStyle(.secondary)
                if let validationError {
                    Text(validationError).font(.caption).foregroundStyle(.red)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        } label: { Label("People you follow", systemImage: "person.2") }
    }
}

struct ReviewInstructionsEditor: View {
    let title: String
    @Binding var text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text(title).font(.callout.weight(.medium))
                Spacer()
                Text("\(text.count) of \(ReviewConfiguration.additionalPromptCharacterLimit) characters")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(text.count > ReviewConfiguration.additionalPromptCharacterLimit ? Color.red : Color.secondary)
            }
            TextEditor(text: $text)
                .font(.system(size: 12))
                .scrollContentBackground(.hidden)
                .frame(height: 70)
                .padding(6)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .accessibilityLabel("\(title) additional review instructions")
        }
    }
}
