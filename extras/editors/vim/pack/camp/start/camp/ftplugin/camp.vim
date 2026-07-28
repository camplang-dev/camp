setlocal commentstring=//\ %s
setlocal tabstop=4
setlocal shiftwidth=4

if executable('camp-lsp') && exists('*lsp#register_server')
  augroup camp_lsp
    autocmd!
    autocmd User lsp_setup call lsp#register_server({
          \ 'name': 'camp-lsp',
          \ 'cmd': {server_info->['camp-lsp']},
          \ 'allowlist': ['camp'],
          \ })
  augroup END
endif
