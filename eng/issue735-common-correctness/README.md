# Common correctness control for the B2 experiment

Initial dev remains `f8ba89121c87a6410a0139c44d7db30eab877f41`. This control contains only the three independently identified correctness corrections:

- #849: capture the send-state epoch under the connection gate (`018728ae87faeda42f07503f1c9bfc70ee564e0c`, including its formatting-only compaction).
- #850: detach a sized codec snapshot before release, preserve a primary failure and return its unpublished writer (`9b6951536d6d1451c694fcf49a1fc73b4994d9d8`). The original 24,624 B full-suite allocation failure remains unassigned; the fixed old/current control matrix is a separate investigation.
- #851: distinguish error completion from already-refunded Abort with one pooled-state boolean (`57b59f40f1c554c11bbfad2ed732073e279dbde9`).

Only StreamFlowController.cs and RpcSession.GeneratedServerBridge.cs differ under src. No directional lock split, B2 grant, B3 ready writer, codec, SendPump, wire, default window or resource-capacity change is included. Future B2 A/B must use this exact common control and exclude all three corrections from its claimed performance effect. Initial-dev measurements remain separately identified.

The runner copies the same three-case combined snapshot/terminal fixture to old and current worktrees, changing no production code. Only class names and using/namespace declarations were combined; the test bodies are those used by the independent corrections. It requires the old three exact symptoms, then four focused and four full 2,085-case current passes with zero skips. It records the actual checkout source, all changes from initial dev, binary identities and original failures. Any unexpected failure stops with the remaining stages unexecuted. The lease-specific eight-case old/current evidence is retained separately in #849.

This is a matched experimental control, not a request to merge three pending Draft PRs or a claim that an unresolved allocation red has been explained.
