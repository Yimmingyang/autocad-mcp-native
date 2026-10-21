# 贡献指南

感谢参与 AutoCAD MCP Native Bridge。

- 复现问题时注明 AutoCAD 完整版本/更新号、Windows、Node 和 Codex 版本及最小步骤。
- 不提交客户 DWG/DXF、凭据、个人目录、AutoCAD SDK DLL 或本机配置。
- 提交前运行 `npm ci`、`npm test`、`npm run check`。
- 将模拟测试与实机 AutoCAD smoke test 分开报告。
- 不要未经逐版本构建和实测扩大兼容范围，不要在未提供迁移说明时破坏现有工具 API。
- 提交贡献即同意其按仓库 MIT License 分发。
