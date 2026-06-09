/* Adapted from sheredom/utf8.h and translated to UTF-16 code units.
 * Original library: https://github.com/sheredom/utf8.h
 */

/* This is free and unencumbered software released into the public domain.
 *
 * Anyone is free to copy, modify, publish, use, compile, sell, or
 * distribute this software, either in source code form or as a compiled
 * binary, for any purpose, commercial or non-commercial, and by any
 * means.
 *
 * In jurisdictions that recognize copyright laws, the author or authors
 * of this software dedicate any and all copyright interest in the
 * software to the public domain. We make this dedication for the benefit
 * of the public at large and to the detriment of our heirs and
 * successors. We intend this dedication to be an overt act of
 * relinquishment in perpetuity of all present and future rights to the
 * software under copyright law.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
 * IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
 * OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
 * ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * For more information, please refer to <http://unlicense.org/> */

#ifndef SHEREDOM_UTF16_H_INCLUDED
#define SHEREDOM_UTF16_H_INCLUDED

#if defined(_MSC_VER)
#pragma warning(push)

/* disable warning: no function prototype given: converting '()' to '(void)' */
#pragma warning(disable : 4255)

/* disable warning: '__cplusplus' is not defined as a preprocessor macro,
 * replacing with '0' for '#if/#elif' */
#pragma warning(disable : 4668)

/* disable warning: bytes padding added after construct */
#pragma warning(disable : 4820)
#endif

#if defined(__cplusplus)
#if defined(_MSC_VER)
#define utf16_cplusplus _MSVC_LANG
#else
#define utf16_cplusplus __cplusplus
#endif
#endif

#include <stddef.h>
#include <stdlib.h>
#include <stdint.h>

#if defined(_MSC_VER)
#pragma warning(pop)
#endif

#if defined(_MSC_VER) && (_MSC_VER < 1920)
typedef __int32 utf16_int32_t;
typedef unsigned __int16 utf16_uint16_t;
#else
typedef int32_t utf16_int32_t;
typedef uint16_t utf16_uint16_t;
#endif

#if defined(utf16_cplusplus) && utf16_cplusplus >= 201103L
using utf16_int16_t = char16_t;
#else
typedef utf16_uint16_t utf16_int16_t;
#endif

#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wold-style-cast"
#pragma clang diagnostic ignored "-Wcast-qual"

#if __has_warning("-Wunsafe-buffer-usage")
#pragma clang diagnostic ignored "-Wunsafe-buffer-usage"
#endif
#endif

#ifdef utf16_cplusplus
extern "C" {
#endif

#if defined(__TINYC__)
#define UTF16_ATTRIBUTE(a) __attribute((a))
#else
#define UTF16_ATTRIBUTE(a) __attribute__((a))
#endif

#if defined(_MSC_VER)
#define utf16_nonnull
#define utf16_pure
#define utf16_restrict __restrict
#define utf16_weak __inline
#elif defined(__clang__) || defined(__GNUC__)
#define utf16_nonnull UTF16_ATTRIBUTE(nonnull)
#define utf16_pure UTF16_ATTRIBUTE(pure)
#define utf16_restrict __restrict__
#define utf16_weak UTF16_ATTRIBUTE(weak)
#elif defined(__TINYC__)
#define utf16_nonnull UTF16_ATTRIBUTE(nonnull)
#define utf16_pure UTF16_ATTRIBUTE(pure)
#define utf16_restrict
#define utf16_weak UTF16_ATTRIBUTE(weak)
#elif defined(__IAR_SYSTEMS_ICC__)
#define utf16_nonnull
#define utf16_pure UTF16_ATTRIBUTE(pure)
#define utf16_restrict __restrict
#define utf16_weak UTF16_ATTRIBUTE(weak)
#else
#error Non clang, non gcc, non MSVC, non tcc, non iar compiler found!
#endif

#ifdef utf16_cplusplus
#define utf16_null NULL
#else
#define utf16_null 0
#endif

#if defined(utf16_cplusplus) && utf16_cplusplus >= 201402L && (!defined(_MSC_VER) || (defined(_MSC_VER) && _MSC_VER >= 1910))
#define utf16_constexpr14 constexpr
#define utf16_constexpr14_impl constexpr
#else
/* constexpr and weak are incompatible. so only enable one of them */
#define utf16_constexpr14 utf16_weak
#define utf16_constexpr14_impl
#endif

/* Return less than 0, 0, greater than 0 if src1 < src2, src1 == src2,
 * src1 > src2 respectively, case insensitive. */
utf16_constexpr14 utf16_nonnull utf16_pure int
utf16casecmp(const utf16_int16_t *src1, const utf16_int16_t *src2);

/* Append the UTF-16 string src onto the UTF-16 string dst. */
utf16_nonnull utf16_weak utf16_int16_t *
utf16cat(utf16_int16_t *utf16_restrict dst,
         const utf16_int16_t *utf16_restrict src);

/* Find the first match of the Unicode code point chr in the UTF-16 string src. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16chr(const utf16_int16_t *src, utf16_int32_t chr);

/* Return less than 0, 0, greater than 0 if src1 < src2,
 * src1 == src2, src1 > src2 respectively. */
utf16_constexpr14 utf16_nonnull utf16_pure int
utf16cmp(const utf16_int16_t *src1, const utf16_int16_t *src2);

/* Copy the UTF-16 string src onto the memory allocated in dst. */
utf16_nonnull utf16_weak utf16_int16_t *
utf16cpy(utf16_int16_t *utf16_restrict dst,
         const utf16_int16_t *utf16_restrict src);

/* Number of UTF-16 code points in the UTF-16 string src that consists entirely
 * of UTF-16 code points not from the UTF-16 string reject. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16cspn(const utf16_int16_t *src, const utf16_int16_t *reject);

/* Duplicate the UTF-16 string src by getting its size, malloc'ing a new buffer,
 * copying over the data, and returning that. Or 0 if malloc failed. */
utf16_weak utf16_int16_t *utf16dup(const utf16_int16_t *src);

/* Number of UTF-16 code points in the UTF-16 string str,
 * excluding the null terminating code unit. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16len(const utf16_int16_t *str);

/* Similar to utf16len, except that only at most n UTF-16 code units of src
 * are looked at. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16nlen(const utf16_int16_t *str, size_t n);

/* Return less than 0, 0, greater than 0 if src1 < src2, src1 == src2,
 * src1 > src2 respectively, case insensitive. Checking at most n UTF-16
 * code units of each string. */
utf16_constexpr14 utf16_nonnull utf16_pure int
utf16ncasecmp(const utf16_int16_t *src1, const utf16_int16_t *src2, size_t n);

/* Append the UTF-16 string src onto the UTF-16 string dst,
 * writing at most n+1 UTF-16 code units. Can produce an invalid UTF-16
 * string if n falls partway through a surrogate pair. */
utf16_nonnull utf16_weak utf16_int16_t *
utf16ncat(utf16_int16_t *utf16_restrict dst,
          const utf16_int16_t *utf16_restrict src, size_t n);

/* Return less than 0, 0, greater than 0 if src1 < src2,
 * src1 == src2, src1 > src2 respectively. Checking at most n UTF-16
 * code units of each string. */
utf16_constexpr14 utf16_nonnull utf16_pure int
utf16ncmp(const utf16_int16_t *src1, const utf16_int16_t *src2, size_t n);

/* Copy the UTF-16 string src onto the memory allocated in dst.
 * Copies at most n UTF-16 code units. If n falls partway through a surrogate
 * pair, or if dst doesn't have enough room for a null terminator, the final
 * string will be cut short to preserve UTF-16 validity. */
utf16_nonnull utf16_weak utf16_int16_t *
utf16ncpy(utf16_int16_t *utf16_restrict dst,
          const utf16_int16_t *utf16_restrict src, size_t n);

/* Similar to utf16dup, except that at most n UTF-16 code units of src are
 * copied. If src is longer than n, only n code units are copied and a null
 * code unit is added.
 *
 * Returns a new string if successful, 0 otherwise. */
utf16_weak utf16_int16_t *utf16ndup(const utf16_int16_t *src, size_t n);

/* Locates the first occurrence in the UTF-16 string str of any code point in
 * the UTF-16 string accept, or 0 if no match was found. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16pbrk(const utf16_int16_t *str, const utf16_int16_t *accept);

/* Find the last match of the Unicode code point chr in the UTF-16 string src. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16rchr(const utf16_int16_t *src, int chr);

/* Number of UTF-16 code units in the UTF-16 string str,
 * including the null terminating code unit. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16size(const utf16_int16_t *str);

/* Similar to utf16size, except that the null terminating code unit is excluded. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16size_lazy(const utf16_int16_t *str);

/* Similar to utf16size, except that only at most n UTF-16 code units of src are
 * looked at and the null terminating code unit is excluded. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16nsize_lazy(const utf16_int16_t *str, size_t n);

/* Number of UTF-16 code points in the UTF-16 string src that consists entirely
 * of UTF-16 code points from the UTF-16 string accept. */
utf16_constexpr14 utf16_nonnull utf16_pure size_t
utf16spn(const utf16_int16_t *src, const utf16_int16_t *accept);

/* The position of the UTF-16 string needle in the UTF-16 string haystack. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16str(const utf16_int16_t *haystack, const utf16_int16_t *needle);

/* The position of the UTF-16 string needle in the UTF-16 string haystack,
 * case insensitive. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16casestr(const utf16_int16_t *haystack, const utf16_int16_t *needle);

/* Return 0 on success, or the position of the invalid UTF-16 code unit on
 * failure. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16valid(const utf16_int16_t *str);

/* Similar to utf16valid, except that only at most n UTF-16 code units of src
 * are looked at. */
utf16_constexpr14 utf16_nonnull utf16_pure utf16_int16_t *
utf16nvalid(const utf16_int16_t *str, size_t n);

/* Given a null-terminated string, makes the string valid by replacing invalid
 * code units with a 1-code-unit replacement. Returns 0 on success. */
utf16_nonnull utf16_weak int utf16makevalid(utf16_int16_t *str,
                                           const utf16_int32_t replacement);

/* Sets out_codepoint to the current UTF-16 code point in str, and returns the
 * address of the next UTF-16 code point after the current one in str. */
utf16_constexpr14 utf16_nonnull utf16_int16_t *
utf16codepoint(const utf16_int16_t *utf16_restrict str,
               utf16_int32_t *utf16_restrict out_codepoint);

/* Calculates the size of the next UTF-16 code point in code units. */
utf16_constexpr14 utf16_nonnull size_t
utf16codepointcalcsize(const utf16_int16_t *str);

/* Returns the size of the given code point in UTF-16 code units. */
utf16_constexpr14 size_t utf16codepointsize(utf16_int32_t chr);

/* Write a code point to the given string, and return the address to the next
 * place after the written code point. Pass how many UTF-16 code units are left
 * in the buffer to n. If there is not enough space for the code point, this
 * function returns null. */
utf16_nonnull utf16_weak utf16_int16_t *
utf16catcodepoint(utf16_int16_t *str, utf16_int32_t chr, size_t n);

/* Returns 1 if the given character is lowercase, or 0 if it is not. */
utf16_constexpr14 int utf16islower(utf16_int32_t chr);

/* Returns 1 if the given character is uppercase, or 0 if it is not. */
utf16_constexpr14 int utf16isupper(utf16_int32_t chr);

/* Transform the given string into all lowercase code points. */
utf16_nonnull utf16_weak void utf16lwr(utf16_int16_t *utf16_restrict str);

/* Transform the given string into all uppercase code points. */
utf16_nonnull utf16_weak void utf16upr(utf16_int16_t *utf16_restrict str);

/* Make a code point lower case if possible. */
utf16_constexpr14 utf16_int32_t utf16lwrcodepoint(utf16_int32_t cp);

/* Make a code point upper case if possible. */
utf16_constexpr14 utf16_int32_t utf16uprcodepoint(utf16_int32_t cp);

/* Sets out_codepoint to the current UTF-16 code point in str, and returns the
 * address of the previous UTF-16 code point before the current one in str. */
utf16_constexpr14 utf16_nonnull utf16_int16_t *
utf16rcodepoint(const utf16_int16_t *utf16_restrict str,
                utf16_int32_t *utf16_restrict out_codepoint);

/* Duplicate the UTF-16 string src by getting its size, calling alloc_func_ptr
 * to copy over data to a new buffer, and returning that. Or 0 if
 * alloc_func_ptr returned null. The allocation size passed is in bytes. */
utf16_weak utf16_int16_t *utf16dup_ex(
    const utf16_int16_t *src,
    utf16_int16_t *(*alloc_func_ptr)(utf16_int16_t *, size_t),
    utf16_int16_t *user_data);

/* Similar to utf16dup, except that at most n UTF-16 code units of src are
 * copied. If src is longer than n, only n code units are copied and a null
 * code unit is added.
 *
 * Returns a new string if successful, 0 otherwise. The allocation size passed
 * to alloc_func_ptr is in bytes. */
utf16_weak utf16_int16_t *utf16ndup_ex(
    const utf16_int16_t *src, size_t n,
    utf16_int16_t *(*alloc_func_ptr)(utf16_int16_t *, size_t),
    utf16_int16_t *user_data);

#undef utf16_weak
#undef utf16_pure
#undef utf16_nonnull

#define UTF16_IS_HIGH_SURROGATE(c) ((0xd800 <= (utf16_int32_t)(c)) && ((utf16_int32_t)(c) <= 0xdbff))
#define UTF16_IS_LOW_SURROGATE(c) ((0xdc00 <= (utf16_int32_t)(c)) && ((utf16_int32_t)(c) <= 0xdfff))
#define UTF16_IS_SURROGATE(c) ((0xd800 <= (utf16_int32_t)(c)) && ((utf16_int32_t)(c) <= 0xdfff))

utf16_constexpr14_impl int utf16casecmp(const utf16_int16_t *src1,
                                        const utf16_int16_t *src2) {
  utf16_int32_t src1_lwr_cp = 0, src2_lwr_cp = 0, src1_upr_cp = 0,
                src2_upr_cp = 0, src1_orig_cp = 0, src2_orig_cp = 0;

  for (;;) {
    src1 = utf16codepoint(src1, &src1_orig_cp);
    src2 = utf16codepoint(src2, &src2_orig_cp);

    src1_lwr_cp = utf16lwrcodepoint(src1_orig_cp);
    src2_lwr_cp = utf16lwrcodepoint(src2_orig_cp);

    src1_upr_cp = utf16uprcodepoint(src1_orig_cp);
    src2_upr_cp = utf16uprcodepoint(src2_orig_cp);

    if ((0 == src1_orig_cp) && (0 == src2_orig_cp)) {
      return 0;
    } else if ((src1_lwr_cp == src2_lwr_cp) ||
               (src1_upr_cp == src2_upr_cp)) {
      continue;
    }

    return src1_lwr_cp - src2_lwr_cp;
  }
}

utf16_int16_t *utf16cat(utf16_int16_t *utf16_restrict dst,
                        const utf16_int16_t *utf16_restrict src) {
  utf16_int16_t *d = dst;

  while (0 != *d) {
    d++;
  }

  while (0 != *src) {
    *d++ = *src++;
  }

  *d = 0;

  return dst;
}

utf16_constexpr14_impl utf16_int16_t *utf16chr(const utf16_int16_t *src,
                                               utf16_int32_t chr) {
  utf16_int32_t cp = 0;

  if (0 == chr) {
    while (0 != *src) {
      src++;
    }
    return (utf16_int16_t *)src;
  }

  while (0 != *src) {
    const utf16_int16_t *const match = src;
    src = utf16codepoint(src, &cp);
    if (cp == chr) {
      return (utf16_int16_t *)match;
    }
  }

  return utf16_null;
}

utf16_constexpr14_impl int utf16cmp(const utf16_int16_t *src1,
                                    const utf16_int16_t *src2) {
  utf16_int32_t src1_cp = 0, src2_cp = 0;

  for (;;) {
    src1 = utf16codepoint(src1, &src1_cp);
    src2 = utf16codepoint(src2, &src2_cp);

    if ((0 == src1_cp) && (0 == src2_cp)) {
      return 0;
    } else if (src1_cp < src2_cp) {
      return -1;
    } else if (src1_cp > src2_cp) {
      return 1;
    }
  }
}

utf16_constexpr14_impl int utf16coll(const utf16_int16_t *src1,
                                     const utf16_int16_t *src2);

utf16_int16_t *utf16cpy(utf16_int16_t *utf16_restrict dst,
                        const utf16_int16_t *utf16_restrict src) {
  utf16_int16_t *d = dst;

  while (0 != *src) {
    *d++ = *src++;
  }

  *d = 0;

  return dst;
}

utf16_constexpr14_impl size_t utf16cspn(const utf16_int16_t *src,
                                        const utf16_int16_t *reject) {
  size_t chars = 0;

  while (0 != *src) {
    const utf16_int16_t *r = reject;
    utf16_int32_t src_cp = 0, reject_cp = 0;
    const utf16_int16_t *const next_src = utf16codepoint(src, &src_cp);

    while (0 != *r) {
      r = utf16codepoint(r, &reject_cp);
      if (src_cp == reject_cp) {
        return chars;
      }
    }

    src = next_src;
    chars++;
  }

  return chars;
}

utf16_int16_t *utf16dup(const utf16_int16_t *src) {
  return utf16dup_ex(src, utf16_null, utf16_null);
}

utf16_int16_t *utf16dup_ex(
    const utf16_int16_t *src,
    utf16_int16_t *(*alloc_func_ptr)(utf16_int16_t *, size_t),
    utf16_int16_t *user_data) {
  utf16_int16_t *n = utf16_null;
  size_t units = utf16size(src);
  size_t i = 0;

  if (alloc_func_ptr) {
    n = alloc_func_ptr(user_data, units * sizeof(utf16_int16_t));
  } else {
#if !defined(UTF16_NO_STD_MALLOC)
    n = (utf16_int16_t *)malloc(units * sizeof(utf16_int16_t));
#else
    return utf16_null;
#endif
  }

  if (utf16_null == n) {
    return utf16_null;
  }

  for (i = 0; i < units; i++) {
    n[i] = src[i];
  }

  return n;
}

utf16_constexpr14_impl utf16_int16_t *utf16fry(const utf16_int16_t *str);

utf16_constexpr14_impl size_t utf16len(const utf16_int16_t *str) {
  return utf16nlen(str, SIZE_MAX);
}

utf16_constexpr14_impl size_t utf16nlen(const utf16_int16_t *str, size_t n) {
  const utf16_int16_t *t = str;
  size_t length = 0;

  while ((size_t)(str - t) < n && 0 != *str) {
    const size_t size = utf16codepointcalcsize(str);
    if ((size_t)(str - t) + size > n) {
      break;
    }
    str += size;
    length++;
  }

  return length;
}

utf16_constexpr14_impl int utf16ncasecmp(const utf16_int16_t *src1,
                                         const utf16_int16_t *src2, size_t n) {
  utf16_int32_t src1_lwr_cp = 0, src2_lwr_cp = 0, src1_upr_cp = 0,
                src2_upr_cp = 0, src1_orig_cp = 0, src2_orig_cp = 0;

  do {
    const utf16_int16_t *const s1 = src1;
    const utf16_int16_t *const s2 = src2;

    if (0 == n) {
      return 0;
    }

    if ((1 == n) && (UTF16_IS_HIGH_SURROGATE(*s1) ||
                     UTF16_IS_HIGH_SURROGATE(*s2))) {
      const utf16_int32_t c1 = (utf16_int32_t)*s1;
      const utf16_int32_t c2 = (utf16_int32_t)*s2;

      if (c1 != c2) {
        return c1 - c2;
      } else {
        return 0;
      }
    }

    src1 = utf16codepoint(src1, &src1_orig_cp);
    src2 = utf16codepoint(src2, &src2_orig_cp);
    n -= utf16codepointsize(src1_orig_cp);

    src1_lwr_cp = utf16lwrcodepoint(src1_orig_cp);
    src2_lwr_cp = utf16lwrcodepoint(src2_orig_cp);

    src1_upr_cp = utf16uprcodepoint(src1_orig_cp);
    src2_upr_cp = utf16uprcodepoint(src2_orig_cp);

    if ((0 == src1_orig_cp) && (0 == src2_orig_cp)) {
      return 0;
    } else if ((src1_lwr_cp == src2_lwr_cp) ||
               (src1_upr_cp == src2_upr_cp)) {
      continue;
    }

    return src1_lwr_cp - src2_lwr_cp;
  } while (0 < n);

  return 0;
}

utf16_int16_t *utf16ncat(utf16_int16_t *utf16_restrict dst,
                         const utf16_int16_t *utf16_restrict src, size_t n) {
  utf16_int16_t *d = dst;

  while (0 != *d) {
    d++;
  }

  while ((0 != *src) && (0 != n--)) {
    *d++ = *src++;
  }

  *d = 0;

  return dst;
}

utf16_constexpr14_impl int utf16ncmp(const utf16_int16_t *src1,
                                     const utf16_int16_t *src2, size_t n) {
  utf16_int32_t src1_cp = 0, src2_cp = 0;

  do {
    const utf16_int16_t *const s1 = src1;
    const utf16_int16_t *const s2 = src2;

    if (0 == n) {
      return 0;
    }

    if ((1 == n) && (UTF16_IS_HIGH_SURROGATE(*s1) ||
                     UTF16_IS_HIGH_SURROGATE(*s2))) {
      if (*s1 < *s2) {
        return -1;
      } else if (*s1 > *s2) {
        return 1;
      } else {
        return 0;
      }
    }

    src1 = utf16codepoint(src1, &src1_cp);
    src2 = utf16codepoint(src2, &src2_cp);
    n -= utf16codepointsize(src1_cp);

    if ((0 == src1_cp) && (0 == src2_cp)) {
      return 0;
    } else if (src1_cp < src2_cp) {
      return -1;
    } else if (src1_cp > src2_cp) {
      return 1;
    }
  } while (0 < n);

  return 0;
}

utf16_int16_t *utf16ncpy(utf16_int16_t *utf16_restrict dst,
                         const utf16_int16_t *utf16_restrict src, size_t n) {
  utf16_int16_t *d = dst;
  size_t index = 0, check_index = 0;

  if (n == 0) {
    return dst;
  }

  for (index = 0; index < n; index++) {
    d[index] = src[index];
    if (0 == src[index]) {
      break;
    }
  }

  if (index > 0) {
    for (check_index = index - 1;
         check_index > 0 && UTF16_IS_LOW_SURROGATE(d[check_index]);
         check_index--) {
      /* just moving the index */
    }

    if (check_index < index) {
      const size_t codepoint_size = UTF16_IS_HIGH_SURROGATE(d[check_index]) ?
                                    (size_t)2 : (size_t)1;
      if (((index - check_index) < codepoint_size) ||
          ((index - check_index) == n)) {
        index = check_index;
      }
    }
  }

  for (; index < n; index++) {
    d[index] = 0;
  }

  return dst;
}

utf16_int16_t *utf16ndup(const utf16_int16_t *src, size_t n) {
  return utf16ndup_ex(src, n, utf16_null, utf16_null);
}

utf16_int16_t *utf16ndup_ex(
    const utf16_int16_t *src, size_t n,
    utf16_int16_t *(*alloc_func_ptr)(utf16_int16_t *, size_t),
    utf16_int16_t *user_data) {
  utf16_int16_t *c = utf16_null;
  size_t units = 0;
  size_t i = 0;

  while ((0 != src[units]) && units < n) {
    units++;
  }

  n = units;

  if (alloc_func_ptr) {
    c = alloc_func_ptr(user_data, (units + 1) * sizeof(utf16_int16_t));
  } else {
#if !defined(UTF16_NO_STD_MALLOC)
    c = (utf16_int16_t *)malloc((units + 1) * sizeof(utf16_int16_t));
#else
    c = utf16_null;
#endif
  }

  if (utf16_null == c) {
    return utf16_null;
  }

  for (i = 0; i < n; i++) {
    c[i] = src[i];
  }

  c[units] = 0;
  return c;
}

utf16_constexpr14_impl utf16_int16_t *utf16rchr(const utf16_int16_t *src,
                                                int chr) {
  utf16_int16_t *match = utf16_null;
  utf16_int32_t cp = 0;

  if (0 == chr) {
    while (0 != *src) {
      src++;
    }
    return (utf16_int16_t *)src;
  }

  while (0 != *src) {
    const utf16_int16_t *const possible = src;
    src = utf16codepoint(src, &cp);
    if (cp == (utf16_int32_t)chr) {
      match = (utf16_int16_t *)possible;
    }
  }

  return match;
}

utf16_constexpr14_impl utf16_int16_t *utf16pbrk(const utf16_int16_t *str,
                                                const utf16_int16_t *accept) {
  while (0 != *str) {
    const utf16_int16_t *a = accept;
    utf16_int32_t str_cp = 0, accept_cp = 0;
    const utf16_int16_t *const next_str = utf16codepoint(str, &str_cp);

    while (0 != *a) {
      a = utf16codepoint(a, &accept_cp);
      if (str_cp == accept_cp) {
        return (utf16_int16_t *)str;
      }
    }

    str = next_str;
  }

  return utf16_null;
}

utf16_constexpr14_impl size_t utf16size(const utf16_int16_t *str) {
  return utf16size_lazy(str) + 1;
}

utf16_constexpr14_impl size_t utf16size_lazy(const utf16_int16_t *str) {
  return utf16nsize_lazy(str, SIZE_MAX);
}

utf16_constexpr14_impl size_t utf16nsize_lazy(const utf16_int16_t *str,
                                              size_t n) {
  size_t size = 0;
  while (size < n && 0 != str[size]) {
    size++;
  }
  return size;
}

utf16_constexpr14_impl size_t utf16spn(const utf16_int16_t *src,
                                       const utf16_int16_t *accept) {
  size_t chars = 0;

  while (0 != *src) {
    const utf16_int16_t *a = accept;
    utf16_int32_t src_cp = 0, accept_cp = 0;
    const utf16_int16_t *const next_src = utf16codepoint(src, &src_cp);
    int found = 0;

    while (0 != *a) {
      a = utf16codepoint(a, &accept_cp);
      if (src_cp == accept_cp) {
        found = 1;
        break;
      }
    }

    if (!found) {
      return chars;
    }

    src = next_src;
    chars++;
  }

  return chars;
}

utf16_constexpr14_impl utf16_int16_t *utf16str(const utf16_int16_t *haystack,
                                               const utf16_int16_t *needle) {
  utf16_int32_t throwaway_codepoint = 0;

  if (0 == *needle) {
    return (utf16_int16_t *)haystack;
  }

  while (0 != *haystack) {
    const utf16_int16_t *maybeMatch = haystack;
    const utf16_int16_t *h = haystack;
    const utf16_int16_t *n = needle;

    while ((*h == *n) && (0 != *h) && (0 != *n)) {
      n++;
      h++;
    }

    if (0 == *n) {
      return (utf16_int16_t *)maybeMatch;
    } else {
      haystack = utf16codepoint(maybeMatch, &throwaway_codepoint);
    }
  }

  return utf16_null;
}

utf16_constexpr14_impl utf16_int16_t *utf16casestr(
    const utf16_int16_t *haystack, const utf16_int16_t *needle) {
  if (0 == *needle) {
    return (utf16_int16_t *)haystack;
  }

  for (;;) {
    const utf16_int16_t *maybeMatch = haystack;
    const utf16_int16_t *n = needle;
    utf16_int32_t h_cp = 0, n_cp = 0;

    const utf16_int16_t *nextH = haystack = utf16codepoint(haystack, &h_cp);
    n = utf16codepoint(n, &n_cp);

    while ((0 != h_cp) && (0 != n_cp)) {
      h_cp = utf16lwrcodepoint(h_cp);
      n_cp = utf16lwrcodepoint(n_cp);

      if (h_cp != n_cp) {
        break;
      }

      haystack = utf16codepoint(haystack, &h_cp);
      n = utf16codepoint(n, &n_cp);
    }

    if (0 == n_cp) {
      return (utf16_int16_t *)maybeMatch;
    }

    if (0 == h_cp) {
      return utf16_null;
    }

    haystack = nextH;
  }
}

utf16_constexpr14_impl utf16_int16_t *utf16valid(
    const utf16_int16_t *str) {
  return utf16nvalid(str, SIZE_MAX);
}

utf16_constexpr14_impl utf16_int16_t *utf16nvalid(const utf16_int16_t *str,
                                                  size_t n) {
  const utf16_int16_t *t = str;
  size_t consumed = 0;

  while ((void)(consumed = (size_t)(str - t)), consumed < n && 0 != *str) {
    const size_t remaining = n - consumed;

    if (UTF16_IS_HIGH_SURROGATE(*str)) {
      if (remaining < 2) {
        return (utf16_int16_t *)str;
      }

      if (!UTF16_IS_LOW_SURROGATE(str[1])) {
        return (utf16_int16_t *)str;
      }

      str += 2;
    } else if (UTF16_IS_LOW_SURROGATE(*str)) {
      return (utf16_int16_t *)str;
    } else {
      str += 1;
    }
  }

  return utf16_null;
}

int utf16makevalid(utf16_int16_t *str, const utf16_int32_t replacement) {
  utf16_int16_t *read = str;
  utf16_int16_t *write = read;
  const utf16_int16_t r = (utf16_int16_t)replacement;

  if ((replacement < 0) || (replacement > 0xffff) ||
      UTF16_IS_SURROGATE(replacement)) {
    return -1;
  }

  while (0 != *read) {
    if (UTF16_IS_HIGH_SURROGATE(*read)) {
      if (UTF16_IS_LOW_SURROGATE(read[1])) {
        *write++ = *read++;
        *write++ = *read++;
      } else {
        *write++ = r;
        read++;
      }
    } else if (UTF16_IS_LOW_SURROGATE(*read)) {
      *write++ = r;
      read++;
    } else {
      *write++ = *read++;
    }
  }

  *write = 0;

  return 0;
}

utf16_constexpr14_impl utf16_int16_t *utf16codepoint(
    const utf16_int16_t *utf16_restrict str,
    utf16_int32_t *utf16_restrict out_codepoint) {
  if (UTF16_IS_HIGH_SURROGATE(str[0]) && UTF16_IS_LOW_SURROGATE(str[1])) {
    *out_codepoint = (utf16_int32_t)(
        0x10000 + (((utf16_int32_t)str[0] - 0xd800) << 10) +
        ((utf16_int32_t)str[1] - 0xdc00));
    str += 2;
  } else {
    *out_codepoint = (utf16_int32_t)str[0];
    str += 1;
  }

  return (utf16_int16_t *)str;
}

utf16_constexpr14_impl size_t utf16codepointcalcsize(
    const utf16_int16_t *str) {
  if (UTF16_IS_HIGH_SURROGATE(str[0]) && UTF16_IS_LOW_SURROGATE(str[1])) {
    return 2;
  }

  return 1;
}

utf16_constexpr14_impl size_t utf16codepointsize(utf16_int32_t chr) {
  if ((chr > 0xffff) && (chr <= 0x10ffff)) {
    return 2;
  }

  return 1;
}

utf16_int16_t *utf16catcodepoint(utf16_int16_t *str, utf16_int32_t chr,
                                 size_t n) {
  if ((chr < 0) || (chr > 0x10ffff)) {
    return utf16_null;
  }

  if (chr <= 0xffff) {
    if (n < 1) {
      return utf16_null;
    }
    str[0] = (utf16_int16_t)chr;
    str += 1;
  } else {
    const utf16_int32_t cp = chr - 0x10000;
    if (n < 2) {
      return utf16_null;
    }
    str[0] = (utf16_int16_t)(0xd800 + (cp >> 10));
    str[1] = (utf16_int16_t)(0xdc00 + (cp & 0x3ff));
    str += 2;
  }

  return str;
}

utf16_constexpr14_impl int utf16islower(utf16_int32_t chr) {
  return chr != utf16uprcodepoint(chr);
}

utf16_constexpr14_impl int utf16isupper(utf16_int32_t chr) {
  return chr != utf16lwrcodepoint(chr);
}

void utf16lwr(utf16_int16_t *utf16_restrict str) {
  utf16_int32_t cp = 0;
  utf16_int16_t *pn = utf16codepoint(str, &cp);

  while (cp != 0) {
    const utf16_int32_t lwr_cp = utf16lwrcodepoint(cp);
    const size_t size = utf16codepointsize(lwr_cp);

    if (lwr_cp != cp) {
      utf16catcodepoint(str, lwr_cp, size);
    }

    str = pn;
    pn = utf16codepoint(str, &cp);
  }
}

void utf16upr(utf16_int16_t *utf16_restrict str) {
  utf16_int32_t cp = 0;
  utf16_int16_t *pn = utf16codepoint(str, &cp);

  while (cp != 0) {
    const utf16_int32_t upr_cp = utf16uprcodepoint(cp);
    const size_t size = utf16codepointsize(upr_cp);

    if (upr_cp != cp) {
      utf16catcodepoint(str, upr_cp, size);
    }

    str = pn;
    pn = utf16codepoint(str, &cp);
  }
}

utf16_constexpr14_impl utf16_int32_t utf16lwrcodepoint(utf16_int32_t cp) {
  if (((0x0041 <= cp) && (0x005a >= cp)) ||
      ((0x00c0 <= cp) && (0x00d6 >= cp)) ||
      ((0x00d8 <= cp) && (0x00de >= cp)) ||
      ((0x0391 <= cp) && (0x03a1 >= cp)) ||
      ((0x03a3 <= cp) && (0x03ab >= cp)) ||
      ((0x0410 <= cp) && (0x042f >= cp))) {
    cp += 32;
  } else if ((0x0400 <= cp) && (0x040f >= cp)) {
    cp += 80;
  } else if (((0x0100 <= cp) && (0x012f >= cp)) ||
             ((0x0132 <= cp) && (0x0137 >= cp)) ||
             ((0x014a <= cp) && (0x0177 >= cp)) ||
             ((0x0182 <= cp) && (0x0185 >= cp)) ||
             ((0x01a0 <= cp) && (0x01a5 >= cp)) ||
             ((0x01de <= cp) && (0x01ef >= cp)) ||
             ((0x01f8 <= cp) && (0x021f >= cp)) ||
             ((0x0222 <= cp) && (0x0233 >= cp)) ||
             ((0x0246 <= cp) && (0x024f >= cp)) ||
             ((0x03d8 <= cp) && (0x03ef >= cp)) ||
             ((0x0460 <= cp) && (0x0481 >= cp)) ||
             ((0x048a <= cp) && (0x04ff >= cp))) {
    cp |= 0x1;
  } else if (((0x0139 <= cp) && (0x0148 >= cp)) ||
             ((0x0179 <= cp) && (0x017e >= cp)) ||
             ((0x01af <= cp) && (0x01b0 >= cp)) ||
             ((0x01b3 <= cp) && (0x01b6 >= cp)) ||
             ((0x01cd <= cp) && (0x01dc >= cp))) {
    cp += 1;
    cp &= ~0x1;
  } else {
    switch (cp) {
    default:
      break;
    case 0x0178:
      cp = 0x00ff;
      break;
    case 0x0243:
      cp = 0x0180;
      break;
    case 0x018e:
      cp = 0x01dd;
      break;
    case 0x023d:
      cp = 0x019a;
      break;
    case 0x0220:
      cp = 0x019e;
      break;
    case 0x01b7:
      cp = 0x0292;
      break;
    case 0x01c4:
      cp = 0x01c6;
      break;
    case 0x01c7:
      cp = 0x01c9;
      break;
    case 0x01ca:
      cp = 0x01cc;
      break;
    case 0x01f1:
      cp = 0x01f3;
      break;
    case 0x01f7:
      cp = 0x01bf;
      break;
    case 0x0187:
      cp = 0x0188;
      break;
    case 0x018b:
      cp = 0x018c;
      break;
    case 0x0191:
      cp = 0x0192;
      break;
    case 0x0198:
      cp = 0x0199;
      break;
    case 0x01a7:
      cp = 0x01a8;
      break;
    case 0x01ac:
      cp = 0x01ad;
      break;
    case 0x01b8:
      cp = 0x01b9;
      break;
    case 0x01bc:
      cp = 0x01bd;
      break;
    case 0x01f4:
      cp = 0x01f5;
      break;
    case 0x023b:
      cp = 0x023c;
      break;
    case 0x0241:
      cp = 0x0242;
      break;
    case 0x03fd:
      cp = 0x037b;
      break;
    case 0x03fe:
      cp = 0x037c;
      break;
    case 0x03ff:
      cp = 0x037d;
      break;
    case 0x037f:
      cp = 0x03f3;
      break;
    case 0x0386:
      cp = 0x03ac;
      break;
    case 0x0388:
      cp = 0x03ad;
      break;
    case 0x0389:
      cp = 0x03ae;
      break;
    case 0x038a:
      cp = 0x03af;
      break;
    case 0x038c:
      cp = 0x03cc;
      break;
    case 0x038e:
      cp = 0x03cd;
      break;
    case 0x038f:
      cp = 0x03ce;
      break;
    case 0x0370:
      cp = 0x0371;
      break;
    case 0x0372:
      cp = 0x0373;
      break;
    case 0x0376:
      cp = 0x0377;
      break;
    case 0x03f4:
      cp = 0x03b8;
      break;
    case 0x03cf:
      cp = 0x03d7;
      break;
    case 0x03f9:
      cp = 0x03f2;
      break;
    case 0x03f7:
      cp = 0x03f8;
      break;
    case 0x03fa:
      cp = 0x03fb;
      break;
    }
  }

  return cp;
}

utf16_constexpr14_impl utf16_int32_t utf16uprcodepoint(utf16_int32_t cp) {
  if (((0x0061 <= cp) && (0x007a >= cp)) ||
      ((0x00e0 <= cp) && (0x00f6 >= cp)) ||
      ((0x00f8 <= cp) && (0x00fe >= cp)) ||
      ((0x03b1 <= cp) && (0x03c1 >= cp)) ||
      ((0x03c3 <= cp) && (0x03cb >= cp)) ||
      ((0x0430 <= cp) && (0x044f >= cp))) {
    cp -= 32;
  } else if ((0x0450 <= cp) && (0x045f >= cp)) {
    cp -= 80;
  } else if (((0x0100 <= cp) && (0x012f >= cp)) ||
             ((0x0132 <= cp) && (0x0137 >= cp)) ||
             ((0x014a <= cp) && (0x0177 >= cp)) ||
             ((0x0182 <= cp) && (0x0185 >= cp)) ||
             ((0x01a0 <= cp) && (0x01a5 >= cp)) ||
             ((0x01de <= cp) && (0x01ef >= cp)) ||
             ((0x01f8 <= cp) && (0x021f >= cp)) ||
             ((0x0222 <= cp) && (0x0233 >= cp)) ||
             ((0x0246 <= cp) && (0x024f >= cp)) ||
             ((0x03d8 <= cp) && (0x03ef >= cp)) ||
             ((0x0460 <= cp) && (0x0481 >= cp)) ||
             ((0x048a <= cp) && (0x04ff >= cp))) {
    cp &= ~0x1;
  } else if (((0x0139 <= cp) && (0x0148 >= cp)) ||
             ((0x0179 <= cp) && (0x017e >= cp)) ||
             ((0x01af <= cp) && (0x01b0 >= cp)) ||
             ((0x01b3 <= cp) && (0x01b6 >= cp)) ||
             ((0x01cd <= cp) && (0x01dc >= cp))) {
    cp -= 1;
    cp |= 0x1;
  } else {
    switch (cp) {
    default:
      break;
    case 0x00ff:
      cp = 0x0178;
      break;
    case 0x0180:
      cp = 0x0243;
      break;
    case 0x01dd:
      cp = 0x018e;
      break;
    case 0x019a:
      cp = 0x023d;
      break;
    case 0x019e:
      cp = 0x0220;
      break;
    case 0x0292:
      cp = 0x01b7;
      break;
    case 0x01c6:
      cp = 0x01c4;
      break;
    case 0x01c9:
      cp = 0x01c7;
      break;
    case 0x01cc:
      cp = 0x01ca;
      break;
    case 0x01f3:
      cp = 0x01f1;
      break;
    case 0x01bf:
      cp = 0x01f7;
      break;
    case 0x0188:
      cp = 0x0187;
      break;
    case 0x018c:
      cp = 0x018b;
      break;
    case 0x0192:
      cp = 0x0191;
      break;
    case 0x0199:
      cp = 0x0198;
      break;
    case 0x01a8:
      cp = 0x01a7;
      break;
    case 0x01ad:
      cp = 0x01ac;
      break;
    case 0x01b9:
      cp = 0x01b8;
      break;
    case 0x01bd:
      cp = 0x01bc;
      break;
    case 0x01f5:
      cp = 0x01f4;
      break;
    case 0x023c:
      cp = 0x023b;
      break;
    case 0x0242:
      cp = 0x0241;
      break;
    case 0x037b:
      cp = 0x03fd;
      break;
    case 0x037c:
      cp = 0x03fe;
      break;
    case 0x037d:
      cp = 0x03ff;
      break;
    case 0x03f3:
      cp = 0x037f;
      break;
    case 0x03ac:
      cp = 0x0386;
      break;
    case 0x03ad:
      cp = 0x0388;
      break;
    case 0x03ae:
      cp = 0x0389;
      break;
    case 0x03af:
      cp = 0x038a;
      break;
    case 0x03cc:
      cp = 0x038c;
      break;
    case 0x03cd:
      cp = 0x038e;
      break;
    case 0x03ce:
      cp = 0x038f;
      break;
    case 0x0371:
      cp = 0x0370;
      break;
    case 0x0373:
      cp = 0x0372;
      break;
    case 0x0377:
      cp = 0x0376;
      break;
    case 0x03d1:
      cp = 0x0398;
      break;
    case 0x03d7:
      cp = 0x03cf;
      break;
    case 0x03f2:
      cp = 0x03f9;
      break;
    case 0x03f8:
      cp = 0x03f7;
      break;
    case 0x03fb:
      cp = 0x03fa;
      break;
    }
  }

  return cp;
}


utf16_constexpr14_impl utf16_int16_t *utf16rcodepoint(
    const utf16_int16_t *utf16_restrict str,
    utf16_int32_t *utf16_restrict out_codepoint) {
  const utf16_int16_t *s = (const utf16_int16_t *)str;

  if (UTF16_IS_HIGH_SURROGATE(s[0]) && UTF16_IS_LOW_SURROGATE(s[1])) {
    *out_codepoint = (utf16_int32_t)(
        0x10000 + (((utf16_int32_t)s[0] - 0xd800) << 10) +
        ((utf16_int32_t)s[1] - 0xdc00));
  } else {
    *out_codepoint = (utf16_int32_t)s[0];
  }

  do {
    s--;
  } while (UTF16_IS_LOW_SURROGATE(s[0]));

  return (utf16_int16_t *)s;
}

#undef UTF16_IS_HIGH_SURROGATE
#undef UTF16_IS_LOW_SURROGATE
#undef UTF16_IS_SURROGATE
#undef utf16_restrict
#undef utf16_constexpr14
#undef utf16_null

#ifdef utf16_cplusplus
} /* extern "C" */
#endif

#if defined(__clang__)
#pragma clang diagnostic pop
#endif

#endif /* SHEREDOM_UTF16_H_INCLUDED */
