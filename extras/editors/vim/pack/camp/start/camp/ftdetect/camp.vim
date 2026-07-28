augroup camp_filetype
  autocmd!
  autocmd BufRead,BufNewFile *.camp setfiletype camp
  autocmd BufRead,BufNewFile *.campbuild setfiletype campbuild
augroup END
