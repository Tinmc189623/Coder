//! `coder-cloud` 服务入口。

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    coder_cloud::run_from_env().await
}
