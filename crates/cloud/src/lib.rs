//! Coder Cloud 服务端库。

use std::{net::SocketAddr, path::PathBuf, sync::Arc, time::Duration};

use anyhow::Context;
use coder_core::model::OpenAiModel;
use metrics_exporter_prometheus::PrometheusBuilder;
use tokio_util::sync::CancellationToken;
use tracing::{info, warn};
use tracing_subscriber::{EnvFilter, layer::SubscriberExt, util::SubscriberInitExt};

pub mod auth;
pub mod config;
pub mod error;
pub mod hub;
pub mod routes;
pub mod state;
pub mod store;

use auth::AuthService;
use config::CloudConfig;
use hub::Hub;
use state::AppState;
use store::{CloudStore, MemoryStore, PostgresStore};

/// 从环境变量和 `CODER_CLOUD_CONFIG` 指定的 TOML 启动云端服务。
pub async fn run_from_env() -> anyhow::Result<()> {
    let config_path = std::env::var_os("CODER_CLOUD_CONFIG").map(PathBuf::from);
    run(config_path).await
}

/// 使用显式配置文件路径启动云端服务；`None` 表示仅使用默认值和环境变量。
pub async fn run(config_path: Option<PathBuf>) -> anyhow::Result<()> {
    init_tracing();
    let config = Arc::new(CloudConfig::load(config_path.as_deref())?);
    let metrics_handle = PrometheusBuilder::new()
        .install_recorder()
        .context("failed to install Prometheus recorder")?;

    let store: Arc<dyn CloudStore> = if config.database_url.trim() == "memory://" {
        warn!("using the in-memory store; all cloud data will be lost on restart");
        Arc::new(MemoryStore::new())
    } else {
        Arc::new(PostgresStore::connect(&config.database_url).await?)
    };

    let auth = Arc::new(AuthService::new(
        config.jwt_secret.expose(),
        config.token_ttl_secs,
    ));
    let model = if config.model_configured() {
        Some(Arc::new(
            OpenAiModel::new(config.model.clone())
                .map_err(|err| anyhow::anyhow!(err.to_string()))?,
        ))
    } else {
        warn!(
            "cloud model proxy is disabled because model.base_url/api_key/model are not configured"
        );
        None
    };
    let hub = Arc::new(Hub::new(store.clone(), config.clone()));
    let state = AppState::new(
        config.clone(),
        store,
        auth,
        hub.clone(),
        model,
        Some(metrics_handle),
    );
    let app = routes::router(state.clone());
    let addr = config.socket_addr()?;
    let shutdown = CancellationToken::new();
    let reaper = tokio::spawn(reaper_loop(state, shutdown.clone()));

    let handle = axum_server::Handle::new();
    let watcher = tokio::spawn(shutdown_watch(
        hub,
        shutdown.clone(),
        handle.clone(),
        Duration::from_secs(config.shutdown_grace_secs),
    ));
    info!(
        %addr,
        tls = config.tls_enabled(),
        database = %redact_database_url(&config.database_url),
        "starting Coder Cloud"
    );

    let result = if config.tls_enabled() {
        let rustls = axum_server::tls_rustls::RustlsConfig::from_pem_file(
            &config.tls_cert_path,
            &config.tls_key_path,
        )
        .await
        .context("failed to load TLS certificate or key")?;
        axum_server::bind_rustls(addr, rustls)
            .handle(handle)
            .serve(app.into_make_service_with_connect_info::<SocketAddr>())
            .await
            .context("cloud server failed")
    } else {
        axum_server::bind(addr)
            .handle(handle)
            .serve(app.into_make_service_with_connect_info::<SocketAddr>())
            .await
            .context("cloud server failed")
    };

    shutdown.cancel();
    watcher.abort();
    let _ = reaper.await;
    info!("Coder Cloud stopped");
    result
}

/// 初始化结构化日志。
fn init_tracing() {
    let filter = EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| EnvFilter::new("coder_cloud=info,tower_http=info"));
    let _ = tracing_subscriber::registry()
        .with(filter)
        .with(tracing_subscriber::fmt::layer().with_target(true))
        .try_init();
}

/// 定期清理过期租约与闲置限速桶。
async fn reaper_loop(state: AppState, shutdown: CancellationToken) {
    let period = Duration::from_secs(state.config.lease_sweep_secs);
    let mut ticker = tokio::time::interval(period);
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    ticker.tick().await;
    loop {
        tokio::select! {
            _ = shutdown.cancelled() => break,
            _ = ticker.tick() => {
                if let Err(err) = state.hub.reap_once().await {
                    warn!(error = %err, "lease reaper failed");
                }
                state.limiter.prune(Duration::from_secs(600));
            }
        }
    }
}

/// 等待退出信号并触发优雅关闭。
async fn shutdown_watch(
    hub: Arc<Hub>,
    shutdown: CancellationToken,
    handle: axum_server::Handle<SocketAddr>,
    grace: Duration,
) {
    shutdown_signal().await;
    info!("shutdown requested; notifying agents");
    hub.broadcast_shutdown("server is shutting down", 5);
    shutdown.cancel();
    handle.graceful_shutdown(Some(grace));
}

/// 监听 Ctrl+C 与 Unix SIGTERM。
async fn shutdown_signal() {
    let ctrl_c = async {
        if let Err(err) = tokio::signal::ctrl_c().await {
            warn!(error = %err, "failed to listen for Ctrl+C");
        }
    };
    #[cfg(unix)]
    let terminate = async {
        match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
            Ok(mut signal) => {
                signal.recv().await;
            }
            Err(err) => warn!(error = %err, "failed to listen for SIGTERM"),
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();
    tokio::select! {
        _ = ctrl_c => {}
        _ = terminate => {}
    }
}

/// 隐藏数据库 URL 中的密码。
fn redact_database_url(url: &str) -> String {
    if url == "memory://" {
        return url.to_owned();
    }
    match url.split_once("://") {
        Some((scheme, rest)) => match rest.split_once('@') {
            Some((credentials, host)) => {
                let user = credentials.split(':').next().unwrap_or("");
                format!("{scheme}://{user}:***@{host}")
            }
            None => url.to_owned(),
        },
        None => "<invalid>".to_owned(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn redacts_database_password() {
        assert_eq!(
            redact_database_url("postgres://coder:secret@db.local:5432/coder"),
            "postgres://coder:***@db.local:5432/coder"
        );
        assert_eq!(redact_database_url("memory://"), "memory://");
    }
}
