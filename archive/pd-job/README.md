# pd-job（已归档）

`pd-job` 曾是包脚本的 Job Object 包装 helper：创建 Job Object（`KILL_ON_JOB_CLOSE`、进程内存/进程数上限），
把脚本进程放进去，并字节转发 stdin/stdout/stderr，超时用 `TerminateJobObject` 杀掉整棵树。

现在脚本统一走 `proc` 扩展的 `proc.spawn`：Job Object、限额、超时、整树终止和 stdio 代理都由它提供，
`pkgmgr` 不再需要这个 helper，也**没有回退路径**（proc 不可用时装包直接报 `ERR_EXT_MISSING`）。

这里保留源码和当时的 e2e 仅作参考：**不再构建、不再部署、不再维护**。

相关：[proc.md](../../docs/proc.md)、[impl-pkgmgr.md](../../docs/impl-pkgmgr.md)。
