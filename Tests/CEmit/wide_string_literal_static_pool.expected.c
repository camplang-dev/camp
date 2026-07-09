// file: wide_string_literal_static_pool.c
#include "wide_string_literal_static_pool_private.h"

/* Private file declarations. */
static void assign(Holder *holder);

static const uint16_t __camp_wstr_0[] = {0x0057, 0x0069, 0x006E, 0x0064, 0x006F, 0x0077, 0};

static void assign(Holder *holder)
{
	holder->text = __camp_wstr_0;
}

int main(void)
{
	Holder holder = (Holder){ 0 };
	assign(&holder);
	return ((holder.text == 0) ? 1 : 0);
}

// file: wide_string_literal_static_pool.h
#ifndef WIDE_STRING_LITERAL_STATIC_POOL_H_
#define WIDE_STRING_LITERAL_STATIC_POOL_H_

#include "wide_string_literal_static_pool_private.h"

int main(void);

#endif
// file: wide_string_literal_static_pool_private.h
#ifndef WIDE_STRING_LITERAL_STATIC_POOL_PRIVATE_H_
#define WIDE_STRING_LITERAL_STATIC_POOL_PRIVATE_H_

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
int main(void);

/* Object declarations. */


#endif
