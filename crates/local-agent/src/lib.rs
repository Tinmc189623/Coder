//! 本地 Agent：负责认证连接、任务幂等执行与断线恢复。

pub mod client;
pub mod config;
pub mod credentials;
pub mod error;
pub mod ledger;
pub mod runner;

pub use config::LocalAgentConfig;
pub use credentials::{DeviceCredentials, load_credentials, register_device, store_credentials};
pub use ledger::TaskLedger;
