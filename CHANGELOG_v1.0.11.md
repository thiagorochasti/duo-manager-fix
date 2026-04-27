# Duo Manager Fix v1.0.11 — Changelog Completo (dev vs main)

## Resumo Executivo

A versão 1.0.11 representa uma **refatoração arquitetural completa** do isolamento de gamepads virtuais e do gerenciamento de sessões RDP. A abordagem evoluiu para uma defesa em profundidade que elimina conflitos com a Steam e resolve problemas históricos de resolução dinâmica e sessões fantasmas.

**Principais Inovações:**
- **Isolamento de Gamepad:** Bloqueio proativo em 3 camadas (Kernel, DACL e Driver Filter).
- **Gerenciamento de Sessão:** Persistência inteligente (SmartSync) e logoff automatizado.
- **Nitidez Visual:** Arredondamento nativo para resoluções múltiplas de 4.

---

## 1. Isolamento Proativo de Gamepads (0ms Windows Race Condition)

### v1.0.11 (dev) — Defesa em Camadas
- **Pre-Creation Blacklist:** O serviço pré-gera 73 IDs na blacklist do HidHide antes mesmo do controle ser criado.
- **DACL de Nível de Kernel (SID S-1-2-1):** Implementamos o bloqueio via permissões de segurança nativas do Windows usando o SID `S-1-2-1` (Console Logon). Isso proíbe que qualquer processo rodando no monitor físico (como a Steam) acesse o controle, mas permite acesso total via RDP/Moonlight.
- **CM_Disable no XUSB:** Desativação instantânea do nó XInput no kernel para quebrar handles agressivos.
- **Watchdog de 50ms:** Polling constante para re-esconder dispositivos caso o driver falhe.

---

## 2. SmartSync: Resolução Dinâmica e Persistência

### Nova Lógica de Troca de Resolução
A v1.0.11-beta resolve a limitação do driver virtual Apollo/IddCx:
- **Persistência Inteligente:** Se você fechar e abrir o Moonlight com a **mesma resolução**, a sessão do Windows permanece intacta (`Disconnected`), permitindo reconexão instantânea sem perda de progresso.
- **Troca de Resolução Física:** Se o Moonlight solicitar uma resolução **diferente**, o sistema executa um **Logoff Forçado** seguido de re-conexão automática (delay de 2s). Isso é necessário porque o monitor virtual físico só aceita redimensionamento real quando a sessão é recriada.
- **Regra do 4 (Nitidez):** O Wrapper agora arredonda larguras (ex: 1366 vira 1368) para satisfazer o protocolo RDP, eliminando borrões na imagem.

---

## 3. Estabilidade do Sistema e Limpeza

### Gerenciamento de Ciclo de Vida
- **Force Logoff (logoff.exe):** Invocamos o comando nativo do Windows para garantir que o usuário "Games" seja totalmente desconectado quando o serviço parar ou a resolução mudar.
- **Job Objects:** Configurado `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` para limpar processos filhos (Sunshine/RDP) instantaneamente se o serviço for interrompido.
- **Fim do Timeout de 3min:** Correção do bug de fuso horário (UTC vs Local) na leitura de logs e redução do timeout inicial para 15 segundos.

---

## 4. HidHide Nativo via IOCTL (HidHideNative.cs)
- **Método:** Acesso IOCTL direto ao driver (~0.1ms) em vez de usar o CLI externo.
- **Resultado:** Zero latência e fim dos deadlocks de handle exclusivo.

---

## checklist de Testes para v1.0.11-beta
- [ ] Conectar Moonlight → Steam do host NÃO detecta controle.
- [ ] Mudar resolução no Moonlight → Sistema faz logoff e volta com a tela nova nítida.
- [ ] Reconectar com mesma resolução → Sessão anterior continua exatamente onde parou.
- [ ] Parar serviço (`sc stop`) → Usuário virtual some do Windows instantaneamente.

---

## Conclusão
A v1.0.11-beta transforma o Duo Manager em uma solução de nível profissional para multi-usuários, garantindo que o jogador remoto e o jogador local nunca interfiram um no outro, mantendo a melhor qualidade visual possível.
