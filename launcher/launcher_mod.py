# -*- coding: utf-8 -*-
r"""SotC-PC Randomizer: o lançador do port com as opções "Randomizer de itens (mod)",
"Randomizer de colossos (mod)", "Wander com roupa da Mono (mod)" e "Som do calanguinho (mod)" na tela Jogar.

A casca (SotC.exe) roda este arquivo com o Python embutido do lançador original:
    SotC.orig.exe --script mod_backup\launcher_mod.py [argumentos]
Ele tira o programa do lançador (sotc_launcher_win) de dentro do SotC.orig.exe, acrescenta as opções
e as guarda em user\launcher_settings.json ("randomizer", "randomizer_colossi", "mod_roupas" e
"mod_calango": true/false). Ao clicar JOGAR (ou com --play):
- com um randomizer ligado, inicia mod_backup\SotC-Randomizer.exe --once --items e/ou --colossi, que
  vigia o jogo e termina com ele;
- os mods de arquivo (roupa da Mono em cachedFiles.psarc, som do calango em pakN.psarc) são aplicados ou
  desfeitos nos psarc do jogo conforme as caixas, com mod_backup\psarc.exe (replace/restore; o índice
  original fica em mod_backup\<arquivo>.psarc.tocbak enquanto o mod está aplicado).
Nada do lançador é alterado em disco; se este arquivo falhar, o lançador original abre do mesmo jeito
(sem as opções) e o motivo fica em mod_backup\launcher_mod.log.
"""
import marshal
import os
import re
import struct
import subprocess
import sys
import time
import zlib
from pathlib import Path

ARGS = sys.argv[1:]          # run_role já tirou "--script <arquivo>"
EXE = sys.executable         # SotC.orig.exe (o lançador original, com o Python)
PORT = Path(EXE).resolve().parent
MOD_DIR = PORT / 'mod_backup'
RANDOMIZER_EXE = MOD_DIR / 'SotC-Randomizer.exe'
PSARC_EXE = MOD_DIR / 'psarc.exe'
MODS_DIR = MOD_DIR / 'mods'            # <mod>\<caminho dentro do psarc> (gravado pelo patch)
LOG = MOD_DIR / 'launcher_mod.log'
KEY = 'randomizer'               # itens
KEY_C = 'randomizer_colossi'     # colossos
KEY_R = 'mod_roupas'             # Wander com roupa da Mono (cachedFiles.psarc)
KEY_S = 'mod_calango'            # som do calanguinho (pakN.psarc)
KEY_M = 'randomizer_coins'       # mapa das 78 moedas (randomizer --coins)
KEY_Z = 'mod_zelda'              # pacote Zelda: skins + Master Sword (psarc), músicas, corações e fôlego (randomizer)
# (chave nas opções, nome no randomizer_mods.txt, argumento do randomizer ou None, pt, en)
ALL_OPTIONS = (
    (KEY, 'items', '--items', 'Itens aleatórios', 'Random items'),
    (KEY_C, 'colossi', '--colossi', 'Colossos aleatórios', 'Random colossus order'),
    (KEY_M, 'coins', '--coins', 'Mapa das 78 moedas', 'Map of the 78 coins'),
    (KEY_R, 'roupas', None, 'Wander com roupa da Mono', "Wander in Mono's dress"),
    (KEY_S, 'calango', None, 'Som do calanguinho', 'Lizard tail sound'),
    # o mod do Zelda é um pacote só: skins + Master Sword, músicas (--music, mod_backup\musicas_zelda) e
    # corações + círculo de fôlego (--hud)
    (KEY_Z, 'zelda', '--music --hud', 'Zelda of the Colossus', 'Zelda of the Colossus'),
)
# mods de arquivo: chave -> (psarc do jogo, pasta em mod_backup\mods). Vários mods podem mexer no mesmo psarc:
# o conjunto aplicado fica em mod_backup\<psarc>.mods (uma pasta por linha) e o índice original em <psarc>.tocbak.
FILE_MODS = {
    KEY_R: ('cachedFiles.psarc', 'roupa_mono'),
    KEY_S: ('pakN.psarc', 'som_calango'),
    # um mod pode mexer em mais de um psarc: lista de (psarc, pasta)
    KEY_Z: [('cachedFiles.psarc', 'zelda_skins/cachedFiles'), ('pak0.psarc', 'zelda_skins/pak0'), ('pakN.psarc', 'zelda_skins/pakN')],
    # the coin map also swaps the fruit-tree map icon textures for a gold coin (randomizer --coins keeps its own logic)
    KEY_M: [('cachedFiles.psarc', 'mapa_moedas/cachedFiles'), ('pakN.psarc', 'mapa_moedas/pakN')],
}
MODS_FILE = MOD_DIR / 'randomizer_mods.txt'   # escrito pelo patch: items=0/1, colossi=0/1, roupas=0/1, calango=0/1
DEFAULT_GAME_DIR = PORT / 'shadow' / 'EP9000-CUSA08809_00-SOTC0000000000EU'


def installed_options():
    """Só as opções que o patch instalou aparecem no lançador (sem o arquivo: os dois randomizers)."""
    flags = {'items': True, 'colossi': True, 'coins': False, 'roupas': False, 'calango': False, 'zelda': None}
    try:
        for line in MODS_FILE.read_text(encoding='utf-8').splitlines():
            k, _sep, v = line.partition('=')
            if k.strip() in flags:
                flags[k.strip()] = v.strip() == '1'
    except OSError:
        pass
    # the Zelda skins are not shipped inside the patcher (380 MB): the option needs the mod folder; without a
    # 'zelda=' line (older patchers) it shows whenever the folder exists
    has_zelda = (MOD_DIR / 'mods' / 'zelda_skins' / 'pakN').is_dir()
    flags['zelda'] = has_zelda and flags['zelda'] is not False
    return tuple(o for o in ALL_OPTIONS if flags[o[1]])


OPTIONS = installed_options()


def log(text):
    try:
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(time.strftime('%H:%M:%S ') + text + '\n')
    except OSError:
        pass


def launcher_code(exe):
    """O programa principal (sotc_launcher_win) guardado no pacote PyInstaller do exe."""
    data = Path(exe).read_bytes()
    pos = data.rfind(b'MEI\014\013\012\013\016')
    if pos < 0:
        raise RuntimeError('pacote PyInstaller não encontrado em ' + exe)
    _magic, len_pkg, toc_off, toc_len, _pyvers, _lib = struct.unpack('!8sIIii64s', data[pos:pos + 88])
    overlay = pos + 88 - len_pkg
    toc = data[overlay + toc_off: overlay + toc_off + toc_len]
    i = 0
    while i < len(toc):
        (size,) = struct.unpack('!i', toc[i:i + 4])
        epos, csize, _usize, cflag, typ, name = struct.unpack('!IIIBc%ds' % (size - 18), toc[i + 4:i + size])
        if typ == b's' and name.rstrip(b'\0') == b'sotc_launcher_win':
            blob = data[overlay + epos: overlay + epos + csize]
            if cflag:
                blob = zlib.decompress(blob)
            return marshal.loads(blob)
        i += size
    raise RuntimeError('sotc_launcher_win não está no pacote de ' + exe)


_randomizer = [None]


def randomizer_running():
    p = _randomizer[0]
    return p is not None and p.poll() is None


def modes(settings):
    """Argumentos de modo do randomizer conforme as opções ligadas ([] = nenhuma). Com qualquer mod ligado
    (também os só de arquivo) o randomizer roda para pôr a marca d'água "Mod by: @AchadoIA" nos menus."""
    args = [a for key, _name, arg, _pt, _en in OPTIONS if arg and settings.get(key) for a in arg.split()]
    if any(settings.get(key) for key, _n, _a, _pt, _en in OPTIONS):
        args.append('--marca')
    return args


def start_randomizer(mode_args):
    """SotC-Randomizer.exe --once <modos>: espera o bb-probe, aplica os patches e termina com o jogo."""
    if not mode_args or randomizer_running():
        return
    if not RANDOMIZER_EXE.is_file():
        log('SotC-Randomizer.exe não encontrado em ' + str(MOD_DIR))
        return
    try:
        _randomizer[0] = subprocess.Popen([str(RANDOMIZER_EXE), '--once'] + mode_args, cwd=str(MOD_DIR),
                                          stdin=subprocess.DEVNULL, close_fds=True,
                                          creationflags=subprocess.CREATE_NO_WINDOW)
        log('randomizer iniciado (pid %d, %s)' % (_randomizer[0].pid, ' '.join(mode_args)))
    except OSError as e:
        log('não foi possível iniciar o randomizer: %s' % e)


def stop_waiting_randomizer(grace=10):
    """Depois de uma sessão --play: o randomizer já termina sozinho quando o jogo fecha; se o
    jogo nem abriu, ele ficaria esperando para sempre."""
    p = _randomizer[0]
    if p is None:
        return
    try:
        p.wait(grace)
    except subprocess.TimeoutExpired:
        p.terminate()


# ---- mods de arquivo (psarc) -------------------------------------------------------------------

def game_dir(settings):
    d = settings.get('game_dir') if isinstance(settings, dict) else None
    if d:
        p = Path(str(d))
        if p.is_dir():
            return p
    return DEFAULT_GAME_DIR


def psarc(*args):
    r"""mod_backup\psarc.exe <args>: (código de saída, saída)."""
    try:
        r = subprocess.run([str(PSARC_EXE)] + [str(a) for a in args], cwd=str(MOD_DIR), stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600,
                           creationflags=subprocess.CREATE_NO_WINDOW)
        return r.returncode, r.stdout.decode('utf-8', 'replace').strip()
    except (OSError, subprocess.SubprocessError) as e:
        return 1, str(e)


def mod_files(folder):
    r"""(nome dentro do psarc, arquivo) de cada arquivo em mod_backup\mods\<pasta>."""
    out = []
    for root, _dirs, files in os.walk(folder):
        for f in files:
            full = Path(root) / f
            out.append((full.relative_to(folder).as_posix(), full))
    return sorted(out)


def applied_mods(archive):
    r"""Pastas de mod aplicadas no psarc segundo mod_backup\<psarc>.mods (None = desconhecido: tocbak sem lista,
    forma antiga de um mod só ou instalação manual)."""
    state = MOD_DIR / (archive + '.mods')
    tocbak = MOD_DIR / (archive + '.tocbak')
    if not tocbak.is_file():
        return []
    try:
        return sorted(l.strip() for l in state.read_text(encoding='utf-8').splitlines() if l.strip())
    except OSError:
        return None


def apply_file_mods(settings):
    """Deixa cada psarc do jogo como as caixas pedem. Por psarc: o conjunto de mods ligados é comparado com o
    aplicado (<psarc>.mods); se difere, o psarc volta ao original (psarc restore) e recebe todos os mods ligados
    numa única troca (psarc replace), que guarda o índice original em <psarc>.tocbak."""
    wanted = [(k, FILE_MODS[k]) for k, _n, _a, _pt, _en in OPTIONS if k in FILE_MODS]
    if not wanted:
        return
    gdir = game_dir(settings)
    if not PSARC_EXE.is_file():
        log('psarc.exe não encontrado em ' + str(MOD_DIR) + '; mods de arquivo não aplicados')
        return
    per_archive = {}
    for key, specs in wanted:
        if isinstance(specs, tuple):
            specs = [specs]
        for archive, folder in specs:
            per_archive.setdefault(archive, [])
            if settings.get(key):
                per_archive[archive].append((key, folder))
    for archive, mods in per_archive.items():
        target = gdir / archive
        tocbak = MOD_DIR / (archive + '.tocbak')
        state = MOD_DIR / (archive + '.mods')
        if not target.is_file():
            log('%s: não existe; nada feito' % target)
            continue
        desired = []
        files = []
        for key, folder in mods:
            fl = mod_files(MODS_DIR / folder)
            if not fl:
                log('%s: pasta %s vazia ou ausente; ignorado' % (key, MODS_DIR / folder))
                continue
            desired.append(folder)
            files += fl
        desired.sort()
        current = applied_mods(archive)
        if current == desired:
            continue
        if tocbak.is_file():
            rc, out = psarc('restore', target, MOD_DIR)
            if rc != 0:
                log('%s: restore falhou (%s); não mexo mais nesse psarc' % (archive, out))
                continue
            try:
                tocbak.unlink()
            except OSError as e:
                log('%s: não apaguei %s (%s)' % (archive, tocbak, e))
            try:
                state.unlink()
            except OSError:
                pass
            log('%s: desfeito (%s)' % (archive, ', '.join(current) if current else 'estado anterior desconhecido'))
        if not desired:
            continue
        # the list goes through a response file (@arquivo): hundreds of textures do not fit a command line
        listfile = MOD_DIR / (archive + '.replace.txt')
        try:
            listfile.write_text('\n'.join('%s=%s' % (name, path) for name, path in files) + '\n', encoding='utf-8')
        except OSError as e:
            log('%s: não gravei %s (%s)' % (archive, listfile, e))
            continue
        rc, out = psarc('replace', target, MOD_DIR, '@' + str(listfile))
        log('%s: aplicado %s (%d arquivos)%s' % (archive, ', '.join(desired), len(files), '' if rc == 0 else ' — ERRO: ' + out[-300:]))
        if rc != 0:
            if tocbak.is_file() and 'backup' in out:
                # o backup foi criado mas a troca falhou: devolve o original
                psarc('restore', target, MOD_DIR)
                try:
                    tocbak.unlink()
                except OSError:
                    pass
            continue
        try:
            state.write_text('\n'.join(desired) + '\n', encoding='utf-8')
        except OSError as e:
            log('%s: não gravei %s (%s)' % (archive, state, e))


def saved_default(config_file):
    """Valor inicial da opção de itens: o que está salvo; sem nada salvo, ligado se as variáveis extras
    antigas tinham RANDOMIZER=1 (a forma anterior do mod)."""
    try:
        text = Path(config_file).read_text(encoding='utf-8')
    except OSError:
        return False
    if re.search(r'"%s"\s*:' % KEY, text):
        return False  # o valor salvo prevalece (load_settings põe por cima do padrão)
    m = re.search(r'"extra_env"\s*:\s*"([^"]*)"', text)
    return bool(m and re.search(r'(^|[\s,;])RANDOMIZER\s*=\s*1\b', m.group(1), re.I))


def install(g):
    """As mudanças sobre o programa do lançador já carregado (dicionário global g)."""
    Launcher = g['Launcher']
    _ = g['_']
    PORT_DIR = g['PORT_DIR']
    g['APP_DEFAULTS'][KEY] = saved_default(g['CONFIG_FILE'])
    for key, _name, _arg, _pt, _en in ALL_OPTIONS:
        g['APP_DEFAULTS'].setdefault(key, False)
    # 0.60.6-plus15 mostra a caixa "Sombras do sol" (fx_sunshadows) mas esqueceu a chave em APP_DEFAULTS, e
    # Launcher.var faz APP_DEFAULTS[key]: o lançador original cai com KeyError. Padrão do changelog: ligado.
    g['APP_DEFAULTS'].setdefault('fx_sunshadows', True)

    # 1. As opções na tela Jogar, logo abaixo de "Modo PS4 Pro": um bloco "Mods" compacto, em duas colunas, numa
    #    única linha da grade (cinco caixas em linhas próprias empurravam o resto da coluna para fora da janela).
    orig_check = Launcher.check

    def check(self, parent, key, title, hint=None):
        if key not in g['APP_DEFAULTS']:
            # proteção contra o mesmo esquecimento em versões futuras: caixa nova sem padrão vem ligada
            g['APP_DEFAULTS'][key] = True
            log('opção %s sem padrão no lançador; assumindo ligada' % key)
        result = orig_check(self, parent, key, title, hint)
        if key == 'neo' and OPTIONS and OPTIONS[0][0] not in self.vars:
            try:
                ttk = self.ttk
                box = ttk.Frame(parent)
                box.grid(row=self.next_row(parent), column=0, columnspan=2, sticky='w', pady=(4, 0))
                ttk.Label(box, text=_('Mods:', 'Mods:'), style='Muted.TLabel').grid(row=0, column=0, columnspan=2, sticky='w')
                for i, (k, _name, _arg, pt, en) in enumerate(OPTIONS):
                    cb = ttk.Checkbutton(box, text=_(pt, en), variable=self.var(k))
                    cb.grid(row=1 + i // 2, column=i % 2, sticky='w', padx=(0, 16), pady=0)
                    self.vars[k].trace_add('write', lambda *_a: self.refresh_status())
            except Exception as e:
                log('caixas dos mods: %s' % e)
        return result
    Launcher.check = check

    # 2. A linha de resumo embaixo ("30 FPS · 2560 × 1440 · ...") mostra o que está ligado.
    orig_summary = Launcher.summary
    short = {KEY: ('itens', 'items'), KEY_C: ('colossos', 'colossi'), KEY_M: ('moedas', 'coins'),
             KEY_R: ('roupa', 'dress'), KEY_S: ('som', 'sound'), KEY_Z: ('zelda', 'zelda')}

    def summary(self):
        text = orig_summary(self)
        try:
            # a barra de baixo: o texto de estado é empacotado antes dos botões JOGAR/Parar e, comprido, os empurrava
            # para fora da janela. Reempacotado por último, é o texto que fica cortado, nunca os botões.
            status = getattr(self, 'status', None)
            if status is not None and not getattr(self, '_mod_status_last', False) and getattr(self, 'play_button', None) is not None:
                status.pack_forget()
                status.pack(side='left', padx=24)
                self._mod_status_last = True
            on = [_(*short[k]) for k, _name, _arg, _pt, _en in OPTIONS if self.var(k).get()]
            if on:
                text = '%s   ·   mods: %s' % (text, ', '.join(on))
        except Exception:
            pass
        return text
    Launcher.summary = summary

    # 3. JOGAR: antes de abrir o jogo, os psarc ficam como as caixas pedem; o lançador salva as opções
    #    (collect) e abre o jogo; com um randomizer ligado o randomizer sobe junto.
    orig_play = Launcher.play

    def play(self):
        try:
            current = dict(self.app)
            for k, _name, _arg, _pt, _en in OPTIONS:
                current[k] = bool(self.var(k).get())
            apply_file_mods(current)
        except Exception as e:
            log('mods de arquivo: %s' % e)
        result = orig_play(self)
        try:
            if getattr(self, 'process', None) is not None:
                start_randomizer(modes(self.app))
        except Exception as e:
            log('play: %s' % e)
        return result
    Launcher.play = play

    # 4. --play (atalho/Steam): o jogo sem a janela.
    orig_play_without_window = g['play_without_window']

    def play_without_window(settings):
        try:
            apply_file_mods(settings)
        except Exception as e:
            log('mods de arquivo: %s' % e)
        start_randomizer(modes(settings))
        try:
            return orig_play_without_window(settings)
        finally:
            stop_waiting_randomizer()
    g['play_without_window'] = play_without_window

    # 5. "Reiniciar" (troca de idioma) e o atalho da área de trabalho passam pela casca SotC.exe,
    #    não pelo SotC.orig.exe, para o mod continuar presente.
    shell = PORT_DIR / 'SotC.exe'
    orig_restart = Launcher.restart

    def restart(self):
        if not shell.is_file():
            return orig_restart(self)
        try:
            subprocess.Popen([str(shell)] + [a for a in sys.argv[1:] if a != '--play'], cwd=str(PORT_DIR))
        except OSError:
            return
        self.root.destroy()
    Launcher.restart = restart

    orig_shortcut = Launcher.shortcut

    def shortcut(self):
        if not shell.is_file():
            return orig_shortcut(self)
        saved = sys.executable
        sys.executable = str(shell)
        try:
            return orig_shortcut(self)
        finally:
            sys.executable = saved
    Launcher.shortcut = shortcut


def run_original():
    """Sem o mod: o lançador original tal qual, mesmos argumentos."""
    return subprocess.call([EXE] + ARGS, cwd=str(PORT))


def main():
    try:
        LOG.write_text('', encoding='utf-8')
    except OSError:
        pass
    try:
        code = launcher_code(EXE)
        g = {'__name__': 'sotc_launcher_win', '__file__': str(PORT / 'sotc_launcher_win.py')}
        sys.argv = [EXE] + ARGS
        exec(code, g)
        install(g)
    except Exception as e:
        log('mod não aplicado (%s: %s); abrindo o lançador original' % (type(e).__name__, e))
        return run_original()
    log('lançador v%s com o mod (opções instaladas: %s; o valor salvo em launcher_settings.json prevalece)'
        % (g.get('VERSION', '?'), ', '.join(o[1] for o in OPTIONS)))
    return g['main']()


if __name__ == '__main__':
    sys.exit(main())
