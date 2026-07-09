// file: wide_string_literal_static_pool.c
#include "wide_string_literal_static_pool_private.h"

/* Private file declarations. */
static void assign(Holder *holder);
static void assignFace(Holder *holder);

static void assign(Holder *holder)
{
	holder->text = u"Window";
}

static void assignFace(Holder *holder)
{
	holder->text = u"a\U0001F600b";
}

int main(void)
{
	Holder holder = (Holder){ 0 };
	assign(&holder);
	assignFace(&holder);
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
