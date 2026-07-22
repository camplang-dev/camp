// file: wide_string_literal_static_pool_fallback.c
#include "wide_string_literal_static_pool_fallback_private.h"

/* Private file declarations. */
static void assign(Holder *holder);
static void assignFace(Holder *holder);

static const uint16_t __camp_wstr_0[] = {0x0057, 0x0069, 0x006E, 0x0064, 0x006F, 0x0077, 0};
static const uint16_t __camp_wstr_1[] = {0x0061, 0xD83D, 0xDE00, 0x0062, 0};

static void assign(Holder *holder)
{
	holder->text = __camp_wstr_0;
}

static void assignFace(Holder *holder)
{
	holder->text = __camp_wstr_1;
}

int32_t main(void)
{
	Holder holder = (Holder){ 0 };
	assign(&holder);
	assignFace(&holder);
	return ((holder.text == 0) ? 1 : 0);
}

// file: wide_string_literal_static_pool_fallback.h
#ifndef WIDE_STRING_LITERAL_STATIC_POOL_FALLBACK_H_
#define WIDE_STRING_LITERAL_STATIC_POOL_FALLBACK_H_

#include "wide_string_literal_static_pool_fallback_private.h"

int32_t main(void);

#endif
// file: wide_string_literal_static_pool_fallback_private.h
#ifndef WIDE_STRING_LITERAL_STATIC_POOL_FALLBACK_PRIVATE_H_
#define WIDE_STRING_LITERAL_STATIC_POOL_FALLBACK_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Holder Holder;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Holder
{
	const uint16_t *text;
};

/* Function declarations. */
int32_t main(void);

/* Object declarations. */


#endif
