# 安全策略

当前公开源码版本为 0.5.0，随仓库最新源码维护。未修复漏洞请通过 GitHub 的 Private Vulnerability Reporting（若启用）或 GitHub 私信联系维护者，不要先发公开 Issue。

MCP 通过 stdio，AutoCAD 插件通过本机 Windows named pipe；不要将管道代理到公网。不要为方便安装全局关闭 AutoCAD 安全加载。报告中避免包含客户图纸、访问令牌、用户目录和个人信息。
