/**
 * Separate database forensics, NOT a security query or semantic-completeness test.
 * Fixed API: github/codeql a1575b98a8cc19bcfa7bb6cb41be688cb3dae65c.
 * Independent result sets intentionally retain contexts lacking optional relations.
 * Decode entity columns with --entities=id. IDs are opaque, database-local, NOT PIDs.
 * No command-line arguments, environment, element text or stack traces are selected.
 */
import csharp
import semmle.code.csharp.commons.Compilation
import semmle.code.csharp.commons.Diagnostics

query predicate contexts(Compilation context) { context = context }

query predicate directories(Compilation context, string path) {
  path = context.getDirectoryString()
}

query predicate outputs(Compilation context, string path) {
  path = context.getOutputAssembly().getFile().getAbsolutePath()
}

query predicate inputs(Compilation context, int index, string path) {
  path = context.getFileCompiled(index).getAbsolutePath()
}

query predicate references(Compilation context, int index, string path) {
  path = context.getReference(index).getAbsolutePath()
}

// Both values derive from compilation_finished, not a zero-error/success assertion.
query predicate finished(Compilation context, float cpuSeconds, float elapsedSeconds) {
  cpuSeconds = context.getCpuSeconds() and elapsedSeconds = context.getElapsedSeconds()
}

query predicate diagnostics(Diagnostic diagnostic, int severity, string tag) {
  severity = diagnostic.getSeverity() and tag = diagnostic.getTag()
}

query predicate diagnosticContexts(Diagnostic diagnostic, Compilation context) {
  context = diagnostic.getCompilation()
}

query predicate diagnosticLocations(Diagnostic diagnostic, string path, int startLine,
  int startColumn, int endLine, int endColumn) {
  diagnostic.getLocation().hasLocationInfo(path, startLine, startColumn, endLine, endColumn)
}

// Extractor messages are global: there is no invented compilation/PID join.
query predicate extractorMessages(ExtractorMessage message, int severity, string origin) {
  severity = message.getSeverity() and origin = message.getOrigin()
}

query predicate extractorLocations(ExtractorMessage message, string path, int startLine,
  int startColumn, int endLine, int endColumn) {
  message.getLocation().hasLocationInfo(path, startLine, startColumn, endLine, endColumn)
}
