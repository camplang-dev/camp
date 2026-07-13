// file: notsupported_omits_c.c
#include "notsupported_omits_c_private.h"


void availableC(void)
{
}

// file: notsupported_omits_c.h
#ifndef NOTSUPPORTED_OMITS_C_H_
#define NOTSUPPORTED_OMITS_C_H_

#include "notsupported_omits_c_private.h"

void availableC(void);

#endif
// file: notsupported_omits_c_private.h
#ifndef NOTSUPPORTED_OMITS_C_PRIVATE_H_
#define NOTSUPPORTED_OMITS_C_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
void availableC(void);

/* Object declarations. */


#endif
