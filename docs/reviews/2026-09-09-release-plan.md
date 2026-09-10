# Reliability release implementation plan

Carry forward the verified review fixes and close the remaining concrete defects before publishing the next release.

1. Bound mounted-file memory, share state between open handles, flush dirty files on volume flush, update paths on rename, and preserve failed writes for recovery.
2. Verify streamed byte counts and completion replies; discard interrupted FTP sessions and prevent incomplete media cache promotion.
3. Bound control API handlers and request duration, propagate cancellation, and drain handlers during shutdown.
4. Make remaining small stores atomic and protect unreadable state. Add conflict detection and recoverable audit evidence to AI configuration commits.
5. Stop producers and drain workers before disposing dependent resources. Preserve connection cooldown behavior; diagnose observed mount failures before changing retry policies.
6. Add behavior tests, run the Release suite and dependency audit, render the UI, validate filesystem operations against a local disposable FTP endpoint, and record any unsupported external integration checks explicitly.
7. Bump the version, document the shipped behavior, commit only reviewed files, push, build/sign release assets, publish, and verify the remote release assets and CI result.

Large-class decomposition will follow the boundaries needed by these fixes. A 24â€“48 hour live-server soak is a post-release observation task, not evidence that can be manufactured by a short automated run.


## Execution status

Steps 1–4 are implemented for the defects described above; failed-close recovery is available, while crash journaling remains follow-up. Step 5 now drains tracked spread work before disposing its resources and uses asynchronous server unmounts; app-wide native shutdown and remote retry diagnosis remain observation work. Step 6 passed 1,230 tests and the disposable native FTPS/WinFsp harness. Step 7 publishes v3.10.111 with the exact reviewed commit and version-specific release notes. Detailed evidence and limitations are recorded in the review closure.
