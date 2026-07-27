# SharpLink 0.8.21 deep audit

Chinese: [`../audit-0.8.21.md`](../audit-0.8.21.md)

Against 0.8.20 commit `726992c`, this batch confirmed five P2-or-higher improvements: shared-memory mapping paths were replacement-decoded before security validation; generated null collections skipped root trailing-byte validation; generated DTO strings and request metadata silently changed isolated UTF-16 surrogates to U+FFFD; and a throwing dynamic per-call scope factory leaked its module call lease.

The complete pre-fix Unit run contained 445 tests: all 441 existing tests passed and exactly four new probes failed. The complete Integration run contained 231 tests: all 230 existing tests passed and the new generated collection probe failed. The fixes reject invalid text before ownership transfer or output, fully consume null collections, and include scope creation in module-lease cleanup. Two zero-reference internal helpers were also removed. A compression output candidate was discarded because writer leases already enforce the hard bound.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 83/83, Unit 445/445, Integration 231/231, the seven-package pack, and fresh-cache package smoke all passed. See [`performance-0.8.21.md`](performance-0.8.21.md) and [`migration-0.8.21.md`](migration-0.8.21.md).
