import ReviewQCore
import SwiftUI

struct DiagnosticsView: View {
    @ObservedObject var diagnostics: DiagnosticsStore
    @State private var confirmClear = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 12) {
                    Image(systemName: "waveform.path")
                        .font(.system(size: 30)).foregroundStyle(.tint)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("ReviewQ Diagnostics").font(.title2.weight(.semibold))
                        Text("Local insight when a refresh does not go to plan.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                }
                GroupBox {
                    VStack(alignment: .leading, spacing: 12) {
                        Toggle("Enable verbose logging", isOn: Binding(
                            get: { diagnostics.verboseEnabled },
                            set: { diagnostics.setVerboseEnabled($0) }
                        ))
                        Text("Warnings, errors, and startup information are saved by default. "
                             + "Verbose logging also records authentication, refreshes, request outcomes, "
                             + "timing, response sizes, and cancellations.")
                            .font(.caption).foregroundStyle(.secondary)
                        LabeledContent(
                            "Log storage", value: diagnostics.storageBytes.formatted(.byteCount(style: .file))
                        )
                    }
                    .frame(maxWidth: .infinity, alignment: .leading).padding(8)
                } label: { Label("Collection", systemImage: "waveform.path") }
                GroupBox {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Button("Export Logs...") { diagnostics.export() }
                                .buttonStyle(.borderedProminent)
                            Spacer()
                            Button("Show in Finder") { Task { await diagnostics.showInFinder() } }
                                .buttonStyle(.bordered)
                        }
                        Button("Clear Logs...", role: .destructive) { confirmClear = true }
                            .buttonStyle(.bordered)
                        Text("Only ReviewQ's two log files and version summary are exported. Nothing is uploaded.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading).padding(8)
                } label: { Label("Local logs", systemImage: "doc.text") }
                if diagnostics.isWorking {
                    ProgressView("Working with local logs...").controlSize(.small)
                }
                if let error = diagnostics.errorMessage {
                    Label(error, systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.red)
                } else if let status = diagnostics.statusMessage {
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
                Text("Privacy and retention").font(.headline)
                Text("Credentials, PR titles and comments, identities, and private URLs are never recorded. "
                     + "Logs do include usage timestamps and app/OS versions. Each file is capped at 5 MiB "
                     + "(10 MiB combined). Records older than seven days are pruned on startup, writes, "
                     + "and export, not while the app is closed. Turning verbose logging off stops future "
                     + "optional collection; use Clear Logs to remove existing records.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(24)
        }
        .frame(minWidth: 520, minHeight: 420)
        .background(Color(nsColor: .windowBackgroundColor))
        .disabled(diagnostics.isWorking)
        .task { await diagnostics.refresh() }
        .onReceive(Timer.publish(every: 2, on: .main, in: .common).autoconnect()) { _ in
            Task { await diagnostics.refresh() }
        }
        .confirmationDialog("Clear local diagnostic logs?", isPresented: $confirmClear, titleVisibility: .visible) {
            Button("Clear Logs", role: .destructive) { Task { await diagnostics.clear() } }
        } message: {
            Text("Existing log records will be removed. Current startup information is retained.")
        }
    }
}
