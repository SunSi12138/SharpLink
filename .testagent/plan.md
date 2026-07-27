# 0.8.27 regression-test plan

1. [x] Prove missing response payloads bypass registered Codecs.
2. [x] Prove consumer stream tokens mask call/lease cancellation.
3. [x] Prove Return/Dispose can retain a writer in a detached pool queue.
4. [x] Prove successful unexpected hosted Server completion leaves the Host running.
5. [x] Prove failed anonymous-pipe connection attempts permit illegal offer reuse.
6. [x] Run the complete pre-fix Unit suite and record the exact failure set.
7. [x] Implement only proven fixes and review assertions/pseudo-mutations.
8. [x] Run exact-final-tree release, package, documentation, and performance gates; prepare the local 0.8.27 commit.
